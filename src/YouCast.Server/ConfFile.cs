using Microsoft.Extensions.Configuration;

namespace YouCast.Server;

/// <summary>
/// Reads a simple "key = value" file (# or ; comments, optional quotes) into the "YouCast:" configuration section.
/// </summary>
public static class ConfFile
{
    public static string DefaultPath =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YouCast", "youcast.conf")
            : "/etc/youcast.conf";

    public static IConfigurationBuilder AddConfFile(this IConfigurationBuilder builder, string path)
    {
        if (!File.Exists(path))
        {
            return builder;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                throw new FormatException($"{path}:{lineNumber}: expected key=value");
            }

            var value = line[(index + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            values[$"YouCast:{line[..index].Trim()}"] = value;
        }

        return builder.AddInMemoryCollection(values);
    }
}
