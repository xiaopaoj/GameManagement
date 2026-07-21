using System.Windows;

namespace GameManagement.Services;

public static class ThemeNames
{
    public const string Classic = "经典主题";
    public const string Windows11 = "Windows 11 风格";
}

public static class ThemeService
{
    public static string Normalize(string? themeName) => themeName == ThemeNames.Windows11 ? ThemeNames.Windows11 : ThemeNames.Classic;

    public static void Apply(string? themeName)
    {
        var normalized = Normalize(themeName);
        var resources = Application.Current.Resources.MergedDictionaries;
        resources.Clear();
        resources.Add(new ResourceDictionary
        {
            Source = new Uri(normalized == ThemeNames.Windows11 ? "Themes/Windows11.xaml" : "Themes/Classic.xaml", UriKind.Relative)
        });
    }
}
