using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace FrameWriter
{
    public struct IntVector2
    {
        public IntVector2(int _x, int _y)
        {
            X = _x;
            Y = _y;
        }
        public int X { get; private set; }
        public int Y { get; private set; }
    }

    public class TestBase
    {
        public short ShortNumber {get;} = 3;
    }
    public class FrameWriterTestingFrame  : TestBase
    {
        public IntVector2[] vectors { get; } = new IntVector2[3] { new IntVector2(2,4), new IntVector2(5,10), new IntVector2(1,0)  };

        public IntVector2 singleVector { get; } = new IntVector2(15, 30);

        [FrameWriterIgnore]
        public int Ignore { get; } = 9;

        [FrameWriterSerializer(nameof(CustomSerializer))]
        public IntVector2 customVector { get; } = new IntVector2(5, 20);

        public static byte[] CustomSerializer(IntVector2 vec)
        {
            return BitConverter.GetBytes(vec.X + vec.Y);
        }
    }

    [Combinator]
    public class FrameWriterTester
    {
        public IObservable<FrameWriterTestingFrame> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(x => { return new FrameWriterTestingFrame(); });
        }
    }
}
