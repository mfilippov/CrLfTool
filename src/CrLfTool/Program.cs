using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CrLfTool
{
  class Program
  {
    private static Regex UnixLineEndingRx = new Regex("([^\r])\n", RegexOptions.Compiled);
    private static Regex WindowsLineEndingRx = new Regex("\r\n", RegexOptions.Compiled);

    static void Main(string[] args)
    {
      MainAsync(args).Wait();
    }

    private async static Task MainAsync(string[] args)
    {
      var result = true;
      if ((args.Length != 3) || (args[0] != "fix" && args[0] != "validate") || (args[1] != "unix" && args[1] != "windows"))
      {
        Console.WriteLine("Usage: CrLfTool fix|validate unix|windows path");
        return;
      }
      else if (!Directory.Exists(args[2]))
      {
        Console.WriteLine("Path should be valid directory");
        return;
      }
      else
      {
        if (!await ProcessDirectory(new DirectoryInfo(args[2]), args[0] == "fix" ? ActionType.Fix : ActionType.Validate, args[1] == "unix" ? LineEnding.Unix : LineEnding.Windows))
        {
          result = false;
        }
      }
      if (!result)
      {
        throw new InvalidDataException("Validation error");
      }
    }
    private static async Task<bool> ProcessDirectory(DirectoryInfo dirInfo, ActionType actionType, LineEnding lineEnding)
    {
      bool result = true;
      if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        foreach (var d in dirInfo.GetDirectories())
        {
          if (!await ProcessDirectory(d, actionType, lineEnding))
          {
            result = false;
          }
        }
        foreach (var f in dirInfo.GetFiles())
        {
          if (!await ProcessFile(f, actionType, lineEnding))
          {
            result = false;
          }
        }
      }
      return result;
    }

    private static async Task<bool> ProcessFile(FileInfo fileInfo, ActionType actionType, LineEnding lineEnding)
    {
      if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && fileInfo.Extension.IsValidExtensionForProcessing())
      {
        if (actionType == ActionType.Fix)
        {
          return await FixFile(fileInfo, lineEnding);
        }
        else if (actionType == ActionType.Validate)
        {
          return await ValidateFile(fileInfo, lineEnding);
        }
      }
      return true;
    }

    private static async Task<bool> FixFile(FileInfo fileInfo, LineEnding lineEnding)
    {
      string content;
      using (var rdr = File.OpenText(fileInfo.FullName))
      {
        content = await rdr.ReadToEndAsync();
        rdr.Close();
      }
      if (lineEnding == LineEnding.Unix)
      {
        content = WindowsLineEndingRx.Replace(content, "\n");
      }
      if (lineEnding == LineEnding.Windows)
      {
        content = UnixLineEndingRx.Replace(content, "$1\r\n");
      }
      using (var wrt = new StreamWriter(fileInfo.FullName))
      {
        await wrt.WriteAsync(content);
        wrt.Close();
      }
      return true;
    }

    private static async Task<bool> ValidateFile(FileInfo fileInfo, LineEnding lineEnding)
    {
      using (var rdr = File.OpenText(fileInfo.FullName))
      {
        var content = await rdr.ReadToEndAsync();
        rdr.Close();
        if (lineEnding == LineEnding.Unix)
        {
          if (WindowsLineEndingRx.Match(content).Success)
          {
            Console.WriteLine($"Invalid line ending in file: {fileInfo.FullName}");
            return false;
          }
        }
        else if (lineEnding == LineEnding.Windows)
        {
          if (UnixLineEndingRx.Match(content).Success)
          {
            Console.WriteLine($"Invalid line ending in file: {fileInfo.FullName}");
            return false;
          }
        }
      }
      return true;
    }
  }

  internal enum ActionType
  {
    Fix,
    Validate
  }

  internal enum LineEnding
  {
    Unix,
    Windows
  }

  internal static class StringExtensions
  {
    public static bool IsValidExtensionForProcessing(this string extension)
    {
      var extensionList = string.IsNullOrEmpty(ConfigurationManager.AppSettings["ExtensionList"])
        ? new List<string> { ".cs", ".cshtml", ".txt", ".js", ".xml" }
        : new List<string>(ConfigurationManager.AppSettings["ExtensionList"].Split(';'));
      return extensionList.Contains(extension);
    }
  }
}
