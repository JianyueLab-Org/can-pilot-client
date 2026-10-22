using NAudio.Wave;

namespace CanClient.Core.Voice;

/// <summary>
/// 接收混音：每个发话者一条独立队列，由输出设备按它实际要的采样数来拉。
///
/// 网络包不当成固定 20 ms 的播放帧：长包这次用不完的尾巴留到下次，短包接着下一个包拼；
/// 所有人在同一个播放时间片里叠加，谁的包短都不会把别人截断。叠加用 int32，最后乘上输出音量
/// 再限幅到 int16，几个人同时说话也不会整数溢出。提示音也混在这里。
///
/// 对应 can-audio 的 rxmix.py（JianyueLab-Org/can-audio#71）。这不是抖动缓冲：每个人刚开口时
/// 垫的那 40 ms 静音只够吸收一点点网络抖动。
/// </summary>
internal sealed class RxMixer : IWaveProvider
{
    /// <summary>每个人最多积压 240 ms，网络一阵突发攒下的包不会变成越来越长的延迟。</summary>
    public const int MaxQueuedSamples = AudioEngine.SampleRate / 1000 * 240;

    /// <summary>刚开口（队列空着）时先垫的静音。</summary>
    public const int LeadInSamples = AudioEngine.SampleRate / 1000 * 40;

    private readonly object _lock = new();
    private readonly Dictionary<uint, TalkerQueue> _talkers = new();
    private readonly List<Clip> _clips = new();
    private int[] _accumulator = Array.Empty<int>();
    private float _gain = 1f;

    public WaveFormat WaveFormat { get; } = new(AudioEngine.SampleRate, 16, 1);

    /// <summary>输出音量（线性倍数），叠加之后、限幅之前乘上去。</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, value);
    }

    /// <summary>正在排队的发话者人数（测试用）。</summary>
    internal int TalkerCount
    {
        get
        {
            lock (_lock)
                return _talkers.Count;
        }
    }

    /// <summary>某个人排队中的采样数（测试用）。</summary>
    internal int Queued(uint talker)
    {
        lock (_lock)
            return _talkers.TryGetValue(talker, out var q) ? q.Count : 0;
    }

    /// <summary>某个人的一段语音进队。数据会被复制，调用方的缓冲可以接着用。</summary>
    public void Enqueue(uint talker, ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty)
            return;
        var copy = pcm.ToArray();
        lock (_lock)
        {
            if (!_talkers.TryGetValue(talker, out var queue))
            {
                queue = new TalkerQueue();
                _talkers[talker] = queue;
            }
            if (queue.Count == 0)
                queue.Add(new short[LeadInSamples]);
            queue.Add(copy);
            queue.TrimTo(MaxQueuedSamples);
        }
    }

    /// <summary>一段一次性的声音（提示音），从下一个时间片开始播。</summary>
    public void AddClip(short[] pcm)
    {
        if (pcm.Length == 0)
            return;
        lock (_lock)
            _clips.Add(new Clip(pcm));
    }

    public void Clear()
    {
        lock (_lock)
        {
            _talkers.Clear();
            _clips.Clear();
        }
    }

    /// <summary>输出设备来拉数据：要多少给多少，没人说话就是静音，永远不返回 0（返回 0 播放会停）。</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        count &= ~1;
        var size = count / 2;
        Mix(buffer.AsSpan(offset, count), size);
        return count;
    }

    private void Mix(Span<byte> output, int size)
    {
        lock (_lock)
        {
            if (_accumulator.Length < size)
                _accumulator = new int[size];
            var acc = _accumulator.AsSpan(0, size);
            acc.Clear();

            List<uint>? drained = null;
            foreach (var (talker, queue) in _talkers)
            {
                queue.TakeInto(acc);
                if (queue.Count == 0)
                    (drained ??= new List<uint>()).Add(talker);
            }
            if (drained is not null)
                foreach (var talker in drained)
                    _talkers.Remove(talker);

            for (var i = _clips.Count - 1; i >= 0; i--)
                if (_clips[i].TakeInto(acc))
                    _clips.RemoveAt(i);

            var gain = Gain;
            for (var i = 0; i < size; i++)
            {
                var v = gain == 1f ? acc[i] : (int)MathF.Round(acc[i] * gain);
                var s = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                output[2 * i] = (byte)s;
                output[2 * i + 1] = (byte)(s >> 8);
            }
        }
    }

    /// <summary>一个人的待播队列：一串网络包，队首那个包可能已经播了一部分。</summary>
    private sealed class TalkerQueue
    {
        private readonly Queue<short[]> _chunks = new();
        private int _headOffset;

        public int Count { get; private set; }

        public void Add(short[] chunk)
        {
            _chunks.Enqueue(chunk);
            Count += chunk.Length;
        }

        /// <summary>超出上限只丢最旧的那部分，留下最新的 <paramref name="max"/> 个采样。</summary>
        public void TrimTo(int max)
        {
            var excess = Count - max;
            while (excess > 0)
            {
                var head = _chunks.Peek();
                var left = head.Length - _headOffset;
                if (left > excess)
                {
                    _headOffset += excess;
                    Count -= excess;
                    return;
                }
                _chunks.Dequeue();
                _headOffset = 0;
                Count -= left;
                excess -= left;
            }
        }

        /// <summary>按顺序取够 <c>acc.Length</c> 个采样叠加进去；包不够长就接着取下一个，最后不够的是静音。</summary>
        public void TakeInto(Span<int> acc)
        {
            var filled = 0;
            while (filled < acc.Length && _chunks.Count > 0)
            {
                var head = _chunks.Peek();
                var n = Math.Min(head.Length - _headOffset, acc.Length - filled);
                for (var i = 0; i < n; i++)
                    acc[filled + i] += head[_headOffset + i];
                filled += n;
                _headOffset += n;
                Count -= n;
                if (_headOffset == head.Length)
                {
                    _chunks.Dequeue();
                    _headOffset = 0;
                }
            }
        }
    }

    private sealed class Clip(short[] samples)
    {
        private int _position;

        /// <summary>叠加下一段；播完了返回 true。</summary>
        public bool TakeInto(Span<int> acc)
        {
            var n = Math.Min(acc.Length, samples.Length - _position);
            for (var i = 0; i < n; i++)
                acc[i] += samples[_position + i];
            _position += n;
            return _position >= samples.Length;
        }
    }
}
