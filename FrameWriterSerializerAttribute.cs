using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrameWriter
{
    /// <summary>
    /// Specifies a custom method to serialize a property into a byte[]
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class FrameWriterSerializerAttribute : Attribute
    {
        public string SerializerMethod { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="FrameWriterSerializerAttribute"/>
        /// class with the specified serializer method name        /// </summary>
        /// <param name="methodName">
        /// The name of the static method to convert from the decorated property
        /// value to byte[]
        /// </param>
        public FrameWriterSerializerAttribute(string methodName)
        {
            SerializerMethod = methodName;
        }
    }
}
