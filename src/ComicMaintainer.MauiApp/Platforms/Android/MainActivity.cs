using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ComicMaintainer.MauiApp;

// ScreenOrientation.FullUser makes the activity honor the device's auto-rotate
// setting: when the user has locked rotation, the app stays fixed; when unlocked,
// it rotates freely. The default (Unspecified) can follow the sensor and ignore the lock.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.FullUser, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
