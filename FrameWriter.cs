using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using OpenCV.Net;
using System.Runtime.InteropServices;
using System.Numerics;
using Bonsai.Reactive;
using System.Net;
using System.Security.Cryptography;
using System.Linq.Expressions;
using OpenEphys.Onix1;
using System.CodeDom;
using System.Drawing.Text;

namespace FrameWriter
{
    [Combinator]
    [Description("")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class FrameWriter
    {
        // We need to make it so this can only be used by classes inheriting from DataFrame and BufferedDataFrame.
        // Easiest way, just a throw if the source is not, but it would be better if we could check at the signature
        // level, so bonsai itself warned before pressing play
        public IObservable<Tsource> Process<Tsource>(IObservable<Tsource> source) where Tsource : class
        {
            PropertyInfo[] properties = typeof(Tsource).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(prop => prop.GetCustomAttribute(typeof(FrameWriterIgnoreAttribute)) == null)
                .OrderBy(prop => prop.MetadataToken).ToArray();

            //prefetch this so it's faster later
            Expression[] writeDelegates = new Expression[properties.Length];
            var inputParam = Expression.Parameter(typeof(Tsource), "input");
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
                    writeDelegate = GetMethodDelegate<Tsource>(method, property, inputParam);
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
                if (writeDelegate == null)
                {
                    throw new InvalidOperationException("Write method not supported for type " + property.PropertyType.ToString());
                }
                writeDelegates[i] = writeDelegate;
            }

            var allDelegates = Expression.Block(writeDelegates);
            Action<Tsource> processParameters = Expression.Lambda<Action<Tsource>>(allDelegates, inputParam).Compile();
            return source.Publish( elm => Observable.Merge(
                elm.Take(1).Do(input => WriteHeader(input, properties)).IgnoreElements(), //NB: IgnoreElements prevents the first element to be repeated at the output
                elm.Do(input =>
                {
                    //For performance we might want to put the input into a queue for another thread to pick it up, 
                    // Update: Gonçalo suggesting this class inherits from StreamSink which would help.
                    processParameters(input);
                })
               ));
        }


        // This is the method that will write the header or the schema in json, whatever we decide
        // since it will be called only once, it is safe to use pure reflection here, isntead of building an
        // expression tree, which would take more time for only a single execution
        private void WriteHeader<T>(T  source, PropertyInfo[] properties) where T : class
        {
            Console.WriteLine("Frame type: " + typeof(T).ToString());
            foreach (var property in properties)
            {
                string sizeStr;
                var propertyType = property.PropertyType;
                var propertyValue = property.GetValue(source, null);

                //TODO: Do proper traversal. If the object is a struct, get the sizes of its members in hierarchical order, etc...
                if (propertyType.IsArray)
                {
                    sizeStr = String.Join(",", Enumerable.Range(0, propertyType.GetArrayRank()).Select(i => (propertyValue as Array).GetLength(i).ToString()).ToArray());
                }
                else
                {
                    sizeStr = "1";
                }
                    Console.WriteLine("\t" + property.Name + ": " + propertyType + " [" + sizeStr + "]");
            }
        }

        
        //This is the method that needs to connect to whatever thread and send them data. Every other type is already converted into byte[]
        // Note that ther might be a bunch of small arrays (for example a Quaternion is 4 arrays of size 4 (4 elements of sizeof(int) = 4 bytes)
        //So some buffering to write in chunks might be desirable, but that is to see.
        // Update: Gonçalo suggesting this class inherits from StreamSink which would help.
        private void Write(byte[] data)
        {
            Console.WriteLine(String.Join(",",data.Select(e => { return e.ToString(); }).ToArray()) + ": " + data.Length);
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
                        expressions[i]= Expression.Call(thisInstance, ((Action<IConvertible>)WriteEnum).Method, Expression.Convert(member,typeof(IConvertible)));
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
        
        
        private Expression GetMethodDelegate<Tsource> (MethodInfo method, PropertyInfo property, Expression inputParam)
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

        private void Write<T>(T[] data) where T : unmanaged
        {
            int size = SizeCache<T>.Size;
            byte[] bytes = new byte[data.Length * size];
            System.Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            Write(bytes);
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
            Write(Convert.ToInt64(data));
        }


        private void Write(Mat mat)
        {
            Write(MatToArray(mat));
        }

        //NB: This should handle other OpenCV classes such as lplImage in a standardized, binary-like way
        private void Write(Arr cvArr)
        {
            Write(MatToArray(cvArr.GetMat()));
        }

        
        
        //NB: This calls classes that implement IConvertible such as Uint64 Int16 etc... 
        private void Write(IConvertible data)
        {
            dynamic dyndata = data;
            Write(BitConverter.GetBytes(dyndata));
        }


        //NB: Quick and dirty copy from Bonsai.Dsp.ArrHelper, since that class is private
        private static byte[] MatToArray(Mat input)
        {
            var step = input.ElementSize * input.Cols;
            var data = new byte[step * input.Rows];
            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Mat dataHeader;

                dataHeader = new Mat(input.Rows, input.Cols, input.Depth, input.Channels, dataHandle.AddrOfPinnedObject(), step);

            }
            finally { dataHandle.Free(); }
            return data;
        }

    }
}
