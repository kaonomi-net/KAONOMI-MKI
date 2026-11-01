using System;
using System.IO;
using KAONOMI.Chat.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KAONOMI.Chat.Services;

/// <summary>Reads and writes <c>screens.yml</c> (next to the executable): the windows to reopen at startup.</summary>
public static class ScreenLayoutStore
{
    private const string FileName = "screens.yml";

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Layout saved by the last <c>/bye</c>, or null when there is none (or it cannot be read).</summary>
    public static ScreenLayout? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ScreenLayout>(File.ReadAllText(FilePath));
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Screens", $"{FileName} not loaded", ex);
            return null;
        }
    }

    /// <summary>Writes <paramref name="layout"/> to <c>screens.yml</c>.</summary>
    public static void Save(ScreenLayout layout)
    {
        try
        {
            var yaml = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .DisableAliases()
                .Build()
                .Serialize(layout);
            File.WriteAllText(FilePath,
                "# Windows of Kaonomi Chat (written by /bye and when the tabs change), reopened as they were at the next start.\n" + yaml);
            ActivityLog.Info("Screens", $"{FileName} saved ({layout.Workspace?.Tabs.Count ?? 0} tab(s), {layout.Windows.Count} window(s))");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Screens", $"{FileName} not saved", ex);
        }
    }
}
