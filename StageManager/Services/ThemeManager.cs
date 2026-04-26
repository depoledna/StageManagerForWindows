using System;
using System.Windows;
using Microsoft.Win32;

namespace StageManager.Services;

public static class ThemeManager
{
    private const string DarkColorsUri = "Themes/DarkColors.xaml";
    private const string LightColorsUri = "Themes/LightColors.xaml";

    public static void ApplyTheme()
    {
        var isLight = IsSystemLightTheme();
        var uri = isLight ? LightColorsUri : DarkColorsUri;
        var newColors = new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };

        var merged = Application.Current.Resources.MergedDictionaries;

        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source;
            if (src != null && (src.OriginalString.Contains("DarkColors") || src.OriginalString.Contains("LightColors")))
                merged.RemoveAt(i);
        }

        merged.Add(newColors);
    }

    public static void StartListening()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void StopListening()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Application.Current?.Dispatcher.BeginInvoke(ApplyTheme);
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }
}
