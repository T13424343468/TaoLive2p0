using System.Text;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using TaoLive.Catalog;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Players;
using TaoLive.Session;

namespace TaoLive.Tv;

/// <summary>
/// 电视端主入口（2.0 W5 接线）：频道列表（编号+分类，电视优先）+ 播放页 SurfaceView，
/// 接会话层（AddTaoLiveSession）与 ExoPlayer 内核（老板 2026-11-15 03:37 换向令）。
/// 操作逻辑沿一期：频道号直达（遥控器数字键）、上下键换台、返回键回列表；
/// 界面样式一期保留（纯文字列表，无花哨横幅）。label=TaoLive 特征关见 AndroidManifest。
/// </summary>
[Activity(Label = "TaoLive", MainLauncher = true)]
public class MainActivity : Activity
{
    private IServiceProvider _services = default!;
    private PlaybackSessionManager _manager = default!;
    private ExoPlayerPlayer _player = default!;
    private readonly IReadOnlyList<Channel> _channels = ChannelTable.Build();

    private FrameLayout _root = default!;
    private ListView _list = default!;
    private FrameLayout _playerPage = default!;
    private readonly SwitchDebouncer _debouncer = new();
    private SurfaceView _surface = default!;
    private TextView _toast = default!;
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _onPlayerPage;
    private int _currentIndex;
    private string _digits = "";
    private Java.Lang.Runnable? _digitCommit;
    private bool _watchdogRunning;
    private Java.Lang.Runnable _toastHide = default!;
    private readonly CntvEpgClient _epg = new();
    private HashSet<string> _favorites = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var resolver = new TicketResolver();
        _player = new ExoPlayerPlayer(this, endpoint => resolver.ResolveAsync(endpoint,
            _player?.CurrentChannelKey ?? "", _player?.CurrentChannelName ?? ""));
        _services = new ServiceCollection()
            .AddSingleton<IPlayer>(_player)
            .AddSingleton<IClock, global::TaoLive.Session.SystemClock>()
            .AddSingleton<IMainThreadDispatcher>(new AndroidMainThreadDispatcher(_main))
            .AddTaoLiveSession()
            .BuildServiceProvider();
        _manager = _services.GetRequiredService<PlaybackSessionManager>();
        _favorites = GetSharedPreferences("favorites", FileCreationMode.Private)!
            .GetStringSet("ids", new List<string>())!.ToHashSet();
        // 备源耗尽提示（一期口径：全源不可达时明确提示，不静默装死；OK 键重试）
        _manager.SessionStateChanged += (_, e) =>
        {
            if (e.State == PlaybackState.Failed)
                ShowToast(TaoLive.Session.SwitchHud.AllLinesFailed);
            else if (e.State == PlaybackState.Buffering)
            {
                // 换台过渡：铺底色遮罩（一期 SwitchRevealPolicy 口径，新流就绪才亮）
                if (SwitchRevealPolicy.ShouldVeilOnSwitch(_surface.Holder?.Surface != null))
                {
                    _surface.Visibility = ViewStates.Invisible;
                    ShowToast(TaoLive.Session.SwitchHud.Connecting);
                }
            }
            else if (e.State == PlaybackState.Playing)
            {
                if (SwitchRevealPolicy.ShouldReveal(true)) _surface.Visibility = ViewStates.Visible;
            }
        };

        // 开机预热（W8：取票口预取进 5 分钟缓存，换台零等待；退避账挂账不烧请求）
        _ = Task.Run(async () =>
        {
            try { await new ChannelWarmer(resolver).WarmAllAsync(ChannelTable.Build()); }
            catch { /* 预热失败不影响播放 */ }
        });

        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        BuildRoot();
        SetContentView(_root);
        ShowList();
    }

    protected override void OnDestroy()
    {
        _manager.Current?.DetachSurface();
        base.OnDestroy();
    }

    // —— 视图 ————————————————————————————————————————————

    private void BuildRoot()
    {
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.Rgb(8, 10, 14));

        _toastHide = new Java.Lang.Runnable(() =>
            _toast.Visibility = ViewStates.Invisible);

        _list = new ListView(this) { CacheColorHint = Color.Transparent };
        _list.Adapter = new ChannelListAdapter(this, _channels, _favorites);
        _list.ItemClick += (_, e) =>
        {
            // 行→频道→列表索引（收藏组行也指向同一频道，编号直达语义不变）
            var channel = ((ChannelListAdapter)_list.Adapter!).ChannelAt(e.Position);
            if (channel is null) return;
            PlayAt(_channels.ToList().FindIndex(c => c.Id == channel.Id));
        };
        _root.AddView(_list, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _playerPage = new FrameLayout(this) { Visibility = ViewStates.Gone };
        _surface = new SurfaceView(this);
        _playerPage.AddView(_surface, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _toast = new TextView(this) { Gravity = GravityFlags.Center };
        _toast.SetTextColor(Color.White);
        _toast.TextSize = 34;
        _toast.SetBackgroundColor(Color.Argb(170, 0, 0, 0));
        _toast.SetPadding(28, 14, 28, 14);
        _playerPage.AddView(_toast, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
        _root.AddView(_playerPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private void ShowList()
    {
        _manager.Current?.DetachSurface();
        _playerPage.Visibility = ViewStates.Gone;
        _list.Visibility = ViewStates.Visible;
        _onPlayerPage = false;
    }

    private void ShowPlayer()
    {
        _list.Visibility = ViewStates.Gone;
        _playerPage.Visibility = ViewStates.Visible;
        _onPlayerPage = true;
        EnsureWatchdog();
        // 播放页已就绪而 Surface 早已创建（返回再进场景）：补挂输出面
        if (_surface.Holder?.Surface != null && _surface.Holder.Surface.IsValid)
            _manager.Current?.AttachSurface(_surface.Holder.Surface.Handle);
    }

    // —— 播放接线 ————————————————————————————————————————————

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        _currentIndex = index;
        SwitchToCurrent();
        ShowPlayer();
    }

    private void SwitchToCurrent()
    {
        var ch = _channels[_currentIndex];
        // 频道上下文先进内核（缓存键+整表行匹配名），再进会话（ChangeChannel 内即触发取票）
        _player.CurrentChannelKey = ch.Id;
        _player.CurrentChannelName = ch.Name;
        if (_manager.Current is { } cur && cur.CurrentChannel?.Id == ch.Id)
        {
            cur.ChangeChannel(ch); // 同频道原地重起播（切回口径）
        }
        else
        {
            _manager.SwitchTo(ch);
        }
        ShowToast(BuildChannelToast(ch));
    }

    /// <summary>
    /// 频道提示文案：频道名 + 官方节目单（现在播/接下来；官方无数据的台不出节目不硬造，一期口径）。
    /// </summary>
    private string BuildChannelToast(Channel ch)
    {
        var text = new StringBuilder(ch.Name);
        try
        {
            if (_epg.Fetch(ch.Id) is { } epg)
            {
                text.Append('\n').Append("正在播: ").Append(epg.Now.Title);
                if (epg.Next.Count > 0)
                    text.Append('\n').Append("接下来: ").Append(epg.Next[0].Title);
            }
        }
        catch { /* EPG 缺失不影响播放 */ }
        return text.ToString();
    }

    private void AttachSurfaceIfShown()
    {
        if (_onPlayerPage && _surface.Holder?.Surface is { IsValid: true } surface)
            _manager.Current?.AttachSurface(surface.Handle);
    }

    private sealed class SurfaceHook(MainActivity owner) : Java.Lang.Object, ISurfaceHolderCallback2
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.AttachSurfaceIfShown();

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }

        public void SurfaceDestroyed(ISurfaceHolder holder) =>
            owner._manager.Current?.DetachSurface();

        public void SurfaceRedrawNeeded(ISurfaceHolder holder) { }
    }

    // —— 看门狗泵（会话层 Tick：缓冲超时/卡流自救） ——————————————————

    private void EnsureWatchdog()
    {
        if (_watchdogRunning) return;
        _watchdogRunning = true;
        Pump();
        void Pump()
        {
            _main.PostDelayed(() =>
            {
                if (_onPlayerPage) _manager.Tick();
                Pump();
            }, 3000);
        }
    }

    // —— 遥控器键位（一期操作逻辑：数字直达、上下换台、返回回列表） ——

    public override bool OnKeyDown([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        switch (keyCode)
        {
            case Keycode.DpadUp when _onPlayerPage:
                DebouncedSwitch(Neighbor(-1));
                return true;
            case Keycode.DpadDown when _onPlayerPage:
                DebouncedSwitch(Neighbor(+1));
                return true;
            case Keycode.DpadCenter or Keycode.Enter when _onPlayerPage:
                // 备源耗尽（Failed）时 OK=原地重试；播放中暂无动作
                if (_manager.Current?.State == PlaybackState.Failed) PlayAt(_currentIndex);
                return true;
            case Keycode.Back or Keycode.Escape when _onPlayerPage:
                ShowList();
                return true;
            case Keycode.Back or Keycode.Escape:
                ConfirmExit(); // 列表页返回：退出对话框（一期 ExitDialogFocus 口径）
                return true;
            case >= Keycode.Num0 and <= Keycode.Num9 when _onPlayerPage:
                FeedDigit((int)keyCode - (int)Keycode.Num0);
                return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyLongPress([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        if (keyCode is Keycode.DpadCenter or Keycode.Enter)
        {
            if (!_onPlayerPage)
            {
                // 列表页长按 OK = 收藏/取消收藏（一期口径）；短按交 ListView 原生点击
                ToggleFavoriteSelected();
                return true;
            }
            // 播放页长按 OK = 切下一线路（W8 换线路入口，环形轮转）
            if (_manager.Current is { LineCount: > 1 } session)
            {
                var next = (session.CurrentLineIndex + 1) % session.LineCount;
                session.SwitchLine(next);
                ShowToast(TaoLive.Session.SwitchHud.LineSwitching(next));
            }
            return true;
        }
        return base.OnKeyLongPress(keyCode, e);
    }

    private void ConfirmExit()
    {
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("TaoLive");
        builder.SetMessage("确定退出吗？");
        builder.SetPositiveButton("确定", (_, _) => Finish());
        builder.SetNegativeButton("取消", (_, _) => { });
        var dialog = builder.Create();
        if (dialog is null) return;
        dialog.Show();
        var ok = dialog.GetButton((int)DialogButtonType.Positive);
        ok?.RequestFocus();
    }

    private void ToggleFavoriteSelected()
    {
        var pos = _list.SelectedItemPosition;
        if (pos < 0) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var channel = adapter.ChannelAt(pos);
        if (channel is null) return;
        if (!_favorites.Add(channel.Id)) _favorites.Remove(channel.Id);
        GetSharedPreferences("favorites", FileCreationMode.Private)!
            .Edit()!.PutStringSet("ids", _favorites)!.Apply();
        adapter.Refresh(_favorites);
    }

    private int Neighbor(int delta)
    {
        var i = (_currentIndex + delta) % _channels.Count;
        return i < 0 ? i + _channels.Count : i;
    }

    /// <summary>换台去抖入口（W8 SwitchDebouncer 口径）：窗口内连按取末次按键，停稳才真切。</summary>
    private void DebouncedSwitch(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        var ch = _channels[index];
        _debouncer.Tap(System.Environment.TickCount64, ch.Id);
        ShowToast(ch.Name);
        _main.PostDelayed(() =>
        {
            if (!_debouncer.HasTarget || !_debouncer.IsDue(System.Environment.TickCount64)) return;
            var key = _debouncer.Target;
            _debouncer.Clear();
            var idx = _channels.ToList().FindIndex(c => c.Id == key);
            if (idx >= 0) PlayAt(idx);
        }, SwitchDebouncer.WindowMs + 20);
    }

    private void FeedDigit(int d)
    {
        _digits = (_digits + d.ToString()).TrimStart('0');
        ShowToast(_digits);
        _digitCommit?.Dispose();
        _digitCommit = new Java.Lang.Runnable(() =>
        {
            if (string.IsNullOrEmpty(_digits)) return;
            var number = int.Parse(_digits);
            _digits = "";
            // 编号=列表顺序（一期口径：编号永久直达）
            if (number >= 1 && number <= _channels.Count) PlayAt(number - 1);
            else ShowToast($"无此频道 {number}");
        });
        _main.PostDelayed(_digitCommit, 1200);
    }

    private static void HideToastCore() { }

    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visibility = ViewStates.Visible;
        _main.RemoveCallbacks(_toastHide);
        _main.PostDelayed(_toastHide, 2200);
    }

    // —— 列表适配器：收藏置顶组 + 分类头 + 编号台名（一期样式：纯文字，编号在左） ——

    private sealed class Row(string title, bool isHeader, Channel? channel)
    {
        public readonly string Title = title;
        public readonly bool IsHeader = isHeader;
        public readonly Channel? Channel = channel;
    }

    private sealed class ChannelListAdapter : BaseAdapter
    {
        private readonly Context _ctx;
        private readonly IReadOnlyList<Channel> _channels;
        private HashSet<string> _favorites;
        private List<Row> _rows;

        public ChannelListAdapter(Context ctx, IReadOnlyList<Channel> channels, HashSet<string> favorites)
        {
            _ctx = ctx;
            _channels = channels;
            _favorites = favorites;
            _rows = BuildRows(channels, favorites);
        }

        private static List<Row> BuildRows(IReadOnlyList<Channel> channels, HashSet<string> favorites)
        {
            var rows = new List<Row>();
            // 收藏置顶组（一期 FavoriteStore 口径：收藏非空才出组，置顶直达）
            var favs = channels.Where(c => favorites.Contains(c.Id)).ToList();
            if (favs.Count > 0)
            {
                rows.Add(new Row("收藏", true, null));
                rows.AddRange(favs.Select(ch => new Row($"★  {ch.Name}", false, ch)));
            }
            foreach (var cat in ChannelTable.CategoryOrder)
            {
                var group = channels.Where(c => c.Category == cat).ToList();
                if (group.Count == 0) continue;
                rows.Add(new Row(cat, true, null));
                // 编号=整表顺序（一期口径：编号永久直达）
                rows.AddRange(group.Select(ch => new Row($"{IndexOfChannel(channels, ch) + 1}  {ch.Name}", false, ch)));
            }
            return rows;
        }

        private static int IndexOfChannel(IReadOnlyList<Channel> list, Channel c)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Id == c.Id) return i;
            }
            return -1;
        }

        /// <summary>行对应的频道；分类头行返回空。</summary>
        public Channel? ChannelAt(int position) =>
            position >= 0 && position < _rows.Count ? _rows[position].Channel : null;

        /// <summary>收藏变化后重建行（保持选中位置不跳）。</summary>
        public void Refresh(HashSet<string> favorites)
        {
            _favorites = favorites;
            _rows = BuildRows(_channels, favorites);
            NotifyDataSetChanged();
        }

        public override int Count => _rows.Count;
        public override Java.Lang.Object GetItem(int position) => new Java.Lang.String(_rows[position].Title);
        public override long GetItemId(int position) => position;
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = _rows[position];
            TextView tv;
            if (convertView is TextView existing)
            {
                tv = existing;
            }
            else
            {
                tv = new TextView(_ctx)
                {
                    TextSize = row.IsHeader ? 18f : 26f,
                };
                tv.SetPadding(40, 22, 40, 22);
            }
            tv.Text = row.Title;
            tv.SetTextColor(Color.White);
            tv.SetBackgroundColor(row.IsHeader ? Color.Argb(255, 20, 24, 32) : Color.Transparent);
            return tv;
        }
    }
}
