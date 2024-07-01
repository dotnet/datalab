using System.Buffers;

namespace Woodstar.Buffers;

interface ICopyableBuffer<T>
{
    void CopyTo<TWriter>(TWriter destination) where TWriter: IBufferWriter<T>;
}
