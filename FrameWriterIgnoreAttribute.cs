using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrameWriter
{
    /// <summary>
    /// Tells the FrameWriter to ignore this property
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class FrameWriterIgnoreAttribute : Attribute
    {
    }
}
