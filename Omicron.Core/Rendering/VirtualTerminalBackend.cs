using System.Buffers;
using System.Runtime.CompilerServices;

namespace Omicron.Core.Rendering;

/// <summary>
///     A virtual terminal backend for tests and headless environments.
///     Captures output bytes and yields synthetic events. No real terminal needed.
/// </summary>
public sealed class VirtualTerminalBackend : ITerminalBackend
{
    private readonly List<byte> _capturedOutput = [];
    private readonly ByteBufferWriter _writer;
    private int _eventIndex;

    public VirtualTerminalBackend()
    {
        _writer = new ByteBufferWriter(_capturedOutput);
    }

    /// <summary>Captured output bytes from the renderer.</summary>
    public IReadOnlyList<byte> CapturedOutput => _capturedOutput;

    /// <summary>Synthetic events to yield from <see cref="ReadEvents" />.</summary>
    public List<TerminalEvent> InjectedEvents { get; } = [];

    public TerminalSize Size { get; set; } = new(80, 24);

    public IBufferWriter<byte> Output => _writer;

    /// <summary>No platform initialization needed for virtual backend.</summary>
    public void Initialize() { }

    public async IAsyncEnumerable<TerminalEvent> ReadEvents(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (_eventIndex < InjectedEvents.Count && !cancellationToken.IsCancellationRequested)
        {
            yield return InjectedEvents[_eventIndex++];
            await Task.CompletedTask;
        }
    }

    public void Flush()
    {
        _writer.Flush();
    }

    public void Dispose()
    {
        _capturedOutput.Clear();
        InjectedEvents.Clear();
    }

    /// <summary>Inject a synthetic event that will be yielded by the next ReadEvents call.</summary>
    public void InjectEvent(TerminalEvent evt)
    {
        InjectedEvents.Add(evt);
    }

    /// <summary>Clear captured output and reset injected events.</summary>
    public void Reset()
    {
        _capturedOutput.Clear();
        InjectedEvents.Clear();
        _eventIndex = 0;
    }

    private sealed class ByteBufferWriter : IBufferWriter<byte>
    {
        private readonly List<byte> _target;
        private byte[] _buffer = new byte[4096];
        private int _written;

        public ByteBufferWriter(List<byte> target)
        {
            _target = target;
        }

        public void Advance(int count)
        {
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_buffer.Length - _written < sizeHint)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _written + sizeHint));
            }
        }

        public void Flush()
        {
            if (_written > 0)
            {
                _target.AddRange(_buffer.AsSpan(0, _written).ToArray());
                _written = 0;
            }
        }
    }
}
