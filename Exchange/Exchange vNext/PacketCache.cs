﻿using Mikodev.Binary.CacheConverters;
using Mikodev.Binary.Common;
using Mikodev.Binary.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Mikodev.Binary
{
    public class PacketCache
    {
        #region static
        private static readonly IReadOnlyList<Converter> defaultConverters;

        static PacketCache()
        {
            var unmanagedTypes = new[]
            {
                typeof(byte),
                typeof(sbyte),
                typeof(char),
                typeof(short),
                typeof(int),
                typeof(long),
                typeof(ushort),
                typeof(uint),
                typeof(ulong),
                typeof(float),
                typeof(double),
            };
            var valueConverters = unmanagedTypes.Select(r => (Converter)Activator.CreateInstance(typeof(UnmanagedValueConverter<>).MakeGenericType(r)));
            var arrayConverters = unmanagedTypes.Select(r => (Converter)Activator.CreateInstance(typeof(UnmanagedArrayConverter<>).MakeGenericType(r)));
            var converters = new List<Converter>();
            converters.AddRange(valueConverters);
            converters.AddRange(arrayConverters);
            converters.Add(new StringConverter());
            defaultConverters = converters;
        }
        #endregion

        private readonly ConcurrentDictionary<Type, Converter> converters;
        private readonly ConcurrentDictionary<string, byte[]> stringCache = new ConcurrentDictionary<string, byte[]>();

        public PacketCache(IEnumerable<Converter> converters = null)
        {
            var dictionary = new ConcurrentDictionary<Type, Converter>();
            if (converters != null)
                foreach (var i in converters)
                    dictionary.TryAdd(i.ValueType, i);
            foreach (var i in defaultConverters)
                dictionary.TryAdd(i.ValueType, i);
            this.converters = dictionary;
        }

        private byte[] GetOrCache(string key)
        {
            if (!stringCache.TryGetValue(key, out var bytes))
                stringCache.TryAdd(key, (bytes = Encoding.UTF8.GetBytes(key)));
            return bytes;
        }

        private Converter GetOrCreateConverter(Type type)
        {
            if (!converters.TryGetValue(type, out var converter))
                converters.TryAdd(type, (converter = CreateConverter(type)));
            return converter;
        }

        private Converter CreateConverter(Type type)
        {
            if (type.IsEnum)
            {
                return (Converter)Activator.CreateInstance(typeof(UnmanagedValueConverter<>).MakeGenericType(type)); // enum
            }

            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1)
                    throw new NotSupportedException("Multidimensional arrays are not supported, use array of arrays instead.");
                var elementType = type.GetElementType();
                if (elementType == typeof(object))
                    goto fail;
                if (elementType.IsEnum)
                    return (Converter)Activator.CreateInstance(typeof(UnmanagedArrayConverter<>).MakeGenericType(elementType)); // enum array
                var converter = GetOrCreateConverter(elementType);
                return (Converter)Activator.CreateInstance(typeof(ArrayConverter<>).MakeGenericType(elementType), converter);
            }

            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
            if (definition == typeof(List<>))
            {
                var elementType = type.GetGenericArguments().Single();
                if (elementType == typeof(object))
                    goto fail;
                var converter = GetOrCreateConverter(elementType);
                return (Converter)Activator.CreateInstance(typeof(ListConverter<>).MakeGenericType(elementType), converter);
            }

            var interfaces = type.IsInterface
                ? type.GetInterfaces().Concat(new[] { type }).ToArray()
                : type.GetInterfaces();
            var collection = interfaces.Where(r => r.IsGenericType && r.GetGenericTypeDefinition() == typeof(ICollection<>)).ToArray();
            if (collection.Length > 1)
                goto fail;
            if (collection.Length == 1)
            {
                var elementType = collection[0].GetGenericArguments().Single();
                if (elementType == typeof(object))
                    goto fail;
                var converter = GetOrCreateConverter(elementType);
                return (Converter)Activator.CreateInstance(typeof(ICollectionConverter<,>).MakeGenericType(type, elementType), converter);
            }

            var enumerable = interfaces.Where(r => r.IsGenericType && r.GetGenericTypeDefinition() == typeof(IEnumerable<>)).ToArray();
            if (enumerable.Length > 1)
                goto fail;
            if (enumerable.Length == 1)
            {
                var elementType = enumerable[0].GetGenericArguments().Single();
                if (elementType == typeof(object))
                    goto fail;
                var converter = GetOrCreateConverter(elementType);
                return (Converter)Activator.CreateInstance(typeof(IEnumerableConverter<,>).MakeGenericType(type, elementType), converter);
            }

            return (Converter)Activator.CreateInstance(typeof(InstanceConverter<>).MakeGenericType(type), ToBytesDelegate(type), ToValueDelegate(type, out var capacity), capacity);
            fail:
            throw new InvalidOperationException($"Invalid collection type: {type}");
        }

        private Delegate ToBytesDelegate(Type type)
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            var instance = Expression.Parameter(type, "instance");
            var allocator = Expression.Parameter(typeof(Allocator), "allocator");
            var stream = Expression.Variable(typeof(UnsafeStream), "stream");
            var position = default(ParameterExpression);
            var variableList = new List<ParameterExpression> { stream };
            var list = new List<Expression> { Expression.Assign(stream, Expression.Field(allocator, nameof(Allocator.stream))) };
            foreach (var i in properties)
            {
                var getMethod = i.GetGetMethod();
                if (getMethod == null || getMethod.GetParameters().Length != 0)
                    continue;
                var propertyType = i.PropertyType;
                var buffer = GetOrCache(i.Name);
                list.Add(Expression.Call(stream, UnsafeStream.WriteExtendMethodInfo, Expression.Constant(buffer)));
                var propertyValue = Expression.Call(instance, getMethod);
                if (position == null)
                    variableList.Add(position = Expression.Variable(typeof(int), "position"));
                list.Add(Expression.Assign(position, Expression.Call(stream, UnsafeStream.BeginModifyMethodInfo)));
                var converter = GetOrCreateConverter(propertyType);
                list.Add(Expression.Call(
                    Expression.Constant(converter),
                    converter.ToBytesDelegate.Method,
                    allocator, propertyValue));
                list.Add(Expression.Call(stream, UnsafeStream.EndModifyMethodInfo, position));
            }
            var block = Expression.Block(variableList, list);
            var expression = Expression.Lambda(block, allocator, instance);
            return expression.Compile();
        }

        private Delegate ToValueDelegateAnonymous(Type type, out int capacity)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length != 1)
                goto fail;

            var constructorInfo = constructors[0];
            var constructorParameters = constructorInfo.GetParameters();
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            if (properties.Length != constructorParameters.Length)
                goto fail;

            for (int i = 0; i < properties.Length; i++)
                if (properties[i].Name != constructorParameters[i].Name || properties[i].PropertyType != constructorParameters[i].ParameterType)
                    goto fail;

            var dictionary = Expression.Parameter(typeof(Dictionary<string, Block>), "dictionary");
            var indexerMethod = typeof(Dictionary<string, Block>).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Single(r => r.PropertyType == typeof(Block) && r.GetIndexParameters().Select(x => x.ParameterType).SequenceEqual(new[] { typeof(string) }))
                .GetGetMethod();
            var expressionArray = new Expression[properties.Length];
            for (int i = 0; i < properties.Length; i++)
            {
                var current = properties[i];
                var converter = GetOrCreateConverter(current.PropertyType);
                var block = Expression.Call(dictionary, indexerMethod, Expression.Constant(current.Name));
                var value = Expression.Call(Expression.Constant(converter), converter.ToValueDelegate.Method, block);
                expressionArray[i] = value;
            }
            var lambda = Expression.Lambda(Expression.New(constructorInfo, expressionArray), dictionary);
            capacity = properties.Length;
            return lambda.Compile();

            fail:
            capacity = 0;
            return null;
        }

        private Delegate ToValueDelegateProperties(Type type, out int capacity)
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            var propertyList = new List<PropertyInfo>();
            for (int i = 0; i < properties.Length; i++)
            {
                var current = properties[i];
                var getter = current.GetGetMethod();
                var setter = current.GetSetMethod();
                if (getter == null || setter == null)
                    continue;
                var setterParameters = setter.GetParameters();
                if (setterParameters == null || setterParameters.Length != 1)
                    continue;
                propertyList.Add(current);
            }
            var dictionary = Expression.Parameter(typeof(Dictionary<string, Block>), "dictionary");
            var indexerMethod = typeof(Dictionary<string, Block>).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Single(r => r.PropertyType == typeof(Block) && r.GetIndexParameters().Select(x => x.ParameterType).SequenceEqual(new[] { typeof(string) }))
                .GetGetMethod();
            var instance = Expression.Variable(type, "instance");
            var expressionList = new List<Expression> { Expression.Assign(instance, Expression.New(type)) };
            foreach (var i in propertyList)
            {
                var converter = GetOrCreateConverter(i.PropertyType);
                var block = Expression.Call(dictionary, indexerMethod, Expression.Constant(i.Name));
                var value = Expression.Call(Expression.Constant(converter), converter.ToValueDelegate.Method, block);
                expressionList.Add(Expression.Call(instance, i.GetSetMethod(), value));
            }
            expressionList.Add(instance);
            var lambda = Expression.Lambda(Expression.Block(new[] { instance }, expressionList), dictionary);
            capacity = propertyList.Count;
            return lambda.Compile();
        }

        private Delegate ToValueDelegate(Type type, out int capacity)
        {
            if (type.IsValueType)
                return ToValueDelegateProperties(type, out capacity);
            var constructorInfo = type.GetConstructor(Type.EmptyTypes);
            return constructorInfo != null ? ToValueDelegateProperties(type, out capacity) : ToValueDelegateAnonymous(type, out capacity);
        }

        public byte[] Serialize<T>(T value)
        {
            var converter = (Converter<T>)GetOrCreateConverter(typeof(T));
            var stream = new UnsafeStream();
            var allocator = new Allocator(stream);
            converter.ToBytes(allocator, value);
            return stream.GetBytes();
        }

        public T Deserialize<T>(byte[] buffer)
        {
            var converter = (Converter<T>)GetOrCreateConverter(typeof(T));
            var block = new Block(buffer);
            var value = converter.ToValue(block);
            return value;
        }

        public T Deserialize<T>(byte[] buffer, T anonymous) => Deserialize<T>(buffer);
    }
}
