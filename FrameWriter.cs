using Bonsai;
using Bonsai.IO;
using OpenCV.Net;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameWriter
{
    [Combinator]
    [Description("")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class FrameWriter : StreamSink<byte[], BinaryWriter>
    {
        Action<TSource> BuildProcessParametersExpression<TSource>(PropertyInfo[] properties)
        {
            //prefetch this so it's faster later
            Expression[] writeDelegates = new Expression[properties.Length];
            var inputParam = Expression.Parameter(typeof(TSource), "input");
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];

                Expression writeDelegate = null;
                MethodInfo method = null;
                if (property.PropertyType.IsEnum)
                {
                    method = ((Action<IConvertible>)WriteEnum).Method;
                }
                else
                {
                    method = this.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == "Write" &&
                                                                                   m.GetParameters().Length == 1 &&
                                                                                   m.GetParameters()[0].ParameterType.IsAssignableFrom(property.PropertyType));
                }
                if (method != null)
                {
                    writeDelegate = GetMethodDelegate<TSource>(method, property, inputParam);
                }
                else if (property.GetCustomAttribute(typeof(FrameWriterSerializerAttribute)) is FrameWriterSerializerAttribute attr)
                {
                    var customMethod = property.DeclaringType.GetMethod(attr.SerializerMethod,
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { property.PropertyType },
                        null);
                    if (customMethod != null)
                    {
                        writeDelegate = BuildCustomWriterExpression(property, inputParam, customMethod);
                    }
                }
                else if (property.PropertyType.IsValueType || (property.PropertyType.IsArray && property.PropertyType.GetElementType().IsValueType))
                {
                    writeDelegate = BuildWriterExpression(property, inputParam);
                }

                writeDelegates[i] = writeDelegate ?? throw new InvalidOperationException("Write method not supported for type " + property.PropertyType.ToString());
            }

            var allDelegates = Expression.Block(writeDelegates);
            return Expression.Lambda<Action<TSource>>(allDelegates, inputParam).Compile();
        }

        bool firstElement;
        int arrayIndex;
        int arrayLength;
        byte[] dataArray;

        // We need to make it so this can only be used by classes inheriting from DataFrame and BufferedDataFrame.
        // Easiest way, just a throw if the source is not, but it would be better if we could check at the signature
        // level, so bonsai itself warned before pressing play
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source) where TSource : class
        {
            PropertyInfo[] properties = typeof(TSource).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(prop => prop.GetCustomAttribute(typeof(FrameWriterIgnoreAttribute)) == null)
                .OrderBy(prop => prop.MetadataToken).ToArray();

            var processParameters = BuildProcessParametersExpression<TSource>(properties);

            firstElement = true;

            return Process(
                source,
                (input) =>
                {
                    arrayIndex = 0;

                    if (firstElement)
                    {
                        byte[] headerAndData = GenerateHeader(input, properties);
                        firstElement = false;
                        dataArray = new byte[arrayLength];
                        processParameters(input);
                        Array.Copy(dataArray, 0, headerAndData, headerAndData.Length - arrayLength, dataArray.Length);
                        return headerAndData;
                    }

                    processParameters(input);
                    return dataArray;
                });
        }

        // This is the method that will write the header or the schema in json, whatever we decide
        // since it will be called only once, it is safe to use pure reflection here, isntead of building an
        // expression tree, which would take more time for only a single execution
        byte[] GenerateHeader<T>(T source, PropertyInfo[] properties) where T : class
        {
            arrayLength = 0;
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(typeof(T).ToString());

            foreach (var property in properties)
            {
                var propertyType = property.PropertyType;
                var propertyValue = property.GetValue(source, null);
                int propertySize;

                static int GetElementSize(Type type)
                {
                    if (type.IsPrimitive || type.IsValueType)
                    {
                        return Marshal.SizeOf(type);
                    }
                    else if (type.IsEnum)
                    {
                        return sizeof(long); // NB: See question below on how we are treating the size of enums
                    }
                    else
                        throw new InvalidOperationException();
                }

                if (propertyType.IsArray)
                {
                    var array = propertyValue as Array;

                    propertySize = array.Length * GetElementSize(propertyType.GetElementType());
                }
                else if (propertyType == typeof(Mat))
                {
                    var mat = propertyValue as Mat;
                    propertySize = mat.ElementSize * mat.Cols * mat.Rows;
                }
                else
                {
                    propertySize = GetElementSize(propertyType);
                }

                writer.Write("\t" + property.Name + ": " + propertyType + " [" + propertySize + "]");

                arrayLength += propertySize;
            }

            writer.Write("Total size = " + arrayLength.ToString());

            byte[] headerAndData = new byte[ms.Length + arrayLength];
            Array.Copy(ms.ToArray(), headerAndData, ms.Length);

            return headerAndData;
        }


        //This is the method that needs to connect to whatever thread and send them data. Every other type is already converted into byte[]
        // Note that ther might be a bunch of small arrays (for example a Quaternion is 4 arrays of size 4 (4 elements of sizeof(int) = 4 bytes)
        //So some buffering to write in chunks might be desirable, but that is to see.
        // Update: Gonçalo suggesting this class inherits from StreamSink which would help.
        protected override void Write(BinaryWriter writer, byte[] data)
        {
            Console.WriteLine(String.Join(",", data.Select(e => { return e.ToString(); }).ToArray()) + ": " + data.Length);
            writer.Write(data);
        }

        protected override BinaryWriter CreateWriter(Stream stream)
        {
            return new BinaryWriter(stream);
        }

        private Expression BuildCustomWriterExpression(PropertyInfo property, Expression input, MethodInfo customMethod)
        {
            Type type = property.PropertyType;
            Expression propertyVal = Expression.Property(input, property);
            var serialized = Expression.Call(null, customMethod, new[] { propertyVal });

            Expression thisInstance = Expression.Constant(this);
            MethodInfo writeMethd = this.GetType().GetMethod(nameof(Write),
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(byte[]) },
                null);

            return Expression.Call(thisInstance, writeMethd, serialized);
        }

        private Expression BuildWriterExpression(PropertyInfo property, Expression input)
        {
            Type type = property.PropertyType;
            Expression propertyVal = Expression.Property(input, property);

            try
            {
                if (type.IsArray)
                {
                    return BuildArrayWriter(type, propertyVal);
                }
                else
                {
                    return BuildStructWriter(type, propertyVal);
                }
            }
            catch
            {
                return null;
            }
        }

        private Expression BuildStructWriter(Type type, Expression instance)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead);
            var elements = fields.Cast<MemberInfo>().Concat(properties.Cast<MemberInfo>()).OrderBy(x => x.MetadataToken).ToArray();

            var bufferParam = Expression.Parameter(typeof(byte[]), "buffer");
            var thisInstance = Expression.Constant(this);
            Expression[] expressions = new Expression[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                Type elementType = GetMemberType(elements[i]);
                if (!(typeof(IConvertible).IsAssignableFrom(elementType) || (elementType.IsArray && elementType.GetElementType().IsValueType)))
                {
                    throw new InvalidOperationException();
                }

                Expression member = GetMemberExpression(elements[i], instance);

                if (typeof(IConvertible).IsAssignableFrom(elementType))
                {
                    if (type.IsEnum)
                    {
                        expressions[i]= Expression.Call(thisInstance, ((Action<IConvertible>)WriteEnum).Method, Expression.Convert(member, typeof(IConvertible)));
                    }
                    else
                    {
                        expressions[i] = Expression.Call(thisInstance, ((Action<IConvertible>)Write).Method, Expression.Convert(member, typeof(IConvertible)));
                    }
                }
                else
                {
                    if (elementType.IsArray) expressions[i] = BuildArrayWriter(elementType, member);
                    else expressions[i] = BuildStructWriter(elementType, member);
                }
            }
            return Expression.Block(expressions);
        }

        private Expression BuildArrayWriter(Type type, Expression instance)
        {
            MethodInfo genericWriteMethod = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "Write" && m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray)
            .Single();
            MethodInfo byteWriteMethod = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "Write" && !m.IsGenericMethod && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsArray
            && m.GetParameters()[0].ParameterType.GetElementType() == typeof(byte)).Single();
            var elementType = type.GetElementType();
            var thisInstance = Expression.Constant(this);
            if (elementType.IsPrimitive)
            {
                if (elementType == typeof(byte))
                {
                    return Expression.Call(thisInstance, byteWriteMethod, instance);
                }
                else
                {
                    var writeMethod = genericWriteMethod.MakeGenericMethod(elementType);
                    return Expression.Call(thisInstance, writeMethod, instance);
                }
            }
            else
            {
                var element = Expression.Parameter(elementType, "elm");
                var method = BuildStructWriter(elementType, element);
                var i = Expression.Parameter(typeof(int), "i");
                var stopLabel = Expression.Label("stopLoop");
                var body = Expression.Block(new[] { element },
                    Expression.Assign(element, Expression.ArrayIndex(instance, i)),
                    method,
                    Expression.PostIncrementAssign(i)
                    );
                var condition = Expression.LessThan(i, Expression.ArrayLength(instance));
                var ifExpression = Expression.IfThenElse(condition, body, Expression.Break(stopLabel));
                var loop = Expression.Loop(ifExpression, stopLabel);
                return Expression.Block(new[] { i }, Expression.Assign(i, Expression.Constant(0)), loop);

            }
        }


        private Expression GetMethodDelegate<Tsource>(MethodInfo method, PropertyInfo property, Expression inputParam)
        {
            var accessProperty = Expression.Property(inputParam, property);
            Type writeParamType = method.GetParameters()[0].ParameterType;
            Expression convertedParam;
            if (property.PropertyType == writeParamType)
            {
                convertedParam = accessProperty;
            }
            else
            {
                convertedParam = Expression.Convert(accessProperty, writeParamType);
            }
            var instance = Expression.Constant(this);
            return Expression.Call(instance, method, convertedParam);
        }

        void Write(byte[] data)
        {
            Array.Copy(data, 0, dataArray, arrayIndex, data.Length);
            arrayIndex += data.Length;
        }

        private void Write<T>(T[] data) where T : unmanaged
        {
            int size = SizeCache<T>.Size;
            int length = data.Length * size;
            Buffer.BlockCopy(data, 0, dataArray, arrayIndex, length);
            arrayIndex += length;
        }


        private static class SizeCache<T>
        {
            public static readonly int Size = Marshal.SizeOf(typeof(T));
        }

        private static Type GetMemberType(MemberInfo member)
        {
            return member.MemberType switch
            {
                MemberTypes.Property => ((PropertyInfo)member).PropertyType,
                MemberTypes.Field => ((FieldInfo)member).FieldType,
                _ => throw new ArgumentException("Unexpected member type ", nameof(member))

            };
        }

        private static Expression GetMemberExpression(MemberInfo member, Expression instance)
        {
            return member.MemberType switch
            {
                MemberTypes.Property => Expression.Property(instance, (PropertyInfo)member),
                MemberTypes.Field => Expression.Field(instance, (FieldInfo)member),
                _ => throw new InvalidOperationException()
            };
        }


        private void WriteEnum(IConvertible data)
        {
            Write(Convert.ToInt64(data)); // Question; with this, are we converting all Enums to be 8 bytes long?
        }


        private void Write(Mat mat)
        {
            MatToArray(mat);
        }

        //NB: This should handle other OpenCV classes such as lplImage in a standardized, binary-like way
        private void Write(Arr cvArr)
        {
            MatToArray(cvArr.GetMat());
        }

        //NB: This calls classes that implement IConvertible such as Uint64 Int16 etc... 
        private void Write(IConvertible data)
        {
            dynamic dyndata = data;
            Write(BitConverter.GetBytes(dyndata));
        }

        //NB: Modified copy from Bonsai.Dsp.ArrHelper, since that class is private
        unsafe void MatToArray(Mat input)
        {
            var step = input.ElementSize * input.Cols;
            int length = step * input.Rows;
            var dataHandle = GCHandle.Alloc(dataArray, GCHandleType.Pinned);
            try
            {
                Mat dataHeader;
                var arrayPtr = dataHandle.AddrOfPinnedObject();

                dataHeader = new Mat(input.Rows, input.Cols, input.Depth, input.Channels, (IntPtr)((byte*)arrayPtr + arrayIndex), step);
                CV.Copy(input, dataHeader);
            }
            finally { dataHandle.Free(); }
            arrayIndex += length;
        }
    }
}
