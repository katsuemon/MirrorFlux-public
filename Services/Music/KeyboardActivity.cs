using System.Diagnostics;

namespace MirrorFlux.Services.Music;

/// <summary>画面の更新と更新の間に鳴った音を覚えておき、速い音の動きも60fpsで1音ずつ鍵盤に見せる。</summary>
public sealed class KeyboardActivity
{
    /// <summary>チャンネルごとの表示待ちの音と、いま見せている音。</summary>
    private sealed class ChannelActivity
    {
        public readonly Queue<int> Pending = new();
        public int? LastRecorded;
        public bool WasKeyOn;
        public int? Visible;
        public long VisibleUntil;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, ChannelActivity> _activity = [];
    private static readonly long FrameTicks = Math.Max(1, Stopwatch.Frequency / 60);
    private const int MaxPendingNotesPerChannel = 8;

    private static int? DisplayNote(MusicChannelSnapshot channel) => channel.NoteNumber;

    /// <summary>
    /// 再生側の状態からキーオンした音を記録する。
    /// 前回と違う音だけ表示待ちに積む。
    /// </summary>
    public void Record(IEnumerable<MusicChannelSnapshot> channels)
    {
        lock (_sync)
        {
            var seen = new HashSet<string>();
            foreach (var channel in channels)
            {
                seen.Add(channel.Id);
                if (!_activity.TryGetValue(channel.Id, out var state)) _activity[channel.Id] = state = new();
                int? displayNote = DisplayNote(channel);
                if (channel.IsKeyOn && displayNote is int note)
                {
                    // 新しく鳴った音か音程が変わった音だけ、表示待ちに積む。
                    if (!state.WasKeyOn || state.LastRecorded != note)
                    {
                        if (state.Pending.Count == 0 || state.Pending.Last() != note)
                        {
                            // 効果音などで音程が60Hzの画面更新よりずっと速く変わっても、表示が実際の再生から遅れすぎないよう、表示待ちは一定数までにする。
                            if (state.Pending.Count >= MaxPendingNotesPerChannel) state.Pending.Dequeue();
                            state.Pending.Enqueue(note);
                        }
                    }
                    state.WasKeyOn = true;
                    state.LastRecorded = note;
                }
                else
                {
                    state.WasKeyOn = false;
                    state.LastRecorded = null;
                }
            }
            // 今回出てこなかったチャンネルはキーオフ扱いにする。
            foreach (var (id, state) in _activity)
                if (!seen.Contains(id)) { state.WasKeyOn = false; state.LastRecorded = null; }
        }
    }

    /// <summary>表示待ちの音を1フレームずつ取り出し、各チャンネルの鍵盤に光らせる音を決める。</summary>
    public MusicChannelSnapshot[] Apply(MusicChannelSnapshot[] channels, long? timestamp = null)
    {
        lock (_sync)
        {
            long now = timestamp ?? Stopwatch.GetTimestamp();
            return channels.Select(channel =>
            {
                if (!_activity.TryGetValue(channel.Id, out var state)) _activity[channel.Id] = state = new();
                // 見せている音の時間が過ぎたら、次の表示待ち（なければいま鳴っている音）に切り替える。
                if (state.Visible == null || state.VisibleUntil <= now)
                {
                    int? next = state.Pending.Count > 0 ? state.Pending.Dequeue() : channel.IsKeyOn ? DisplayNote(channel) : null;
                    state.Visible = next;
                    state.VisibleUntil = next.HasValue ? now + FrameTicks : now;
                }
                return channel with { KeyboardNotes = state.Visible is int note ? [note] : [] };
            }).ToArray();
        }
    }
    public void Clear() { lock (_sync) _activity.Clear(); }
}
