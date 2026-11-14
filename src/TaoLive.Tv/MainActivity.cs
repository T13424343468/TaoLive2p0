using Android.App;
using Android.OS;

namespace TaoLive.Tv;

/// <summary>
/// 电视净壳入口（M1 W1：只立壳不塞业务；播放页与列表页接线 W5 班）。
/// label=TaoLive 特征关：见 AndroidManifest（老板可见特征，终验必查）。
/// </summary>
[Activity(Label = "TaoLive", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // W1 净壳：无 UI 资源时仅落空布局占位；W5 班接列表页与播放页
        SetContentView(Android.Resource.Layout.SimpleListItem1);
    }
}
