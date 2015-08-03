using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CrLfTool
{
  class Program
  {
    private static Regex UnixLineEndingRx = new Regex("([^\r])\n", RegexOptions.Compiled);
    private static Regex WindowsLineEndingRx = new Regex("\r\n", RegexOptions.Compiled);    

    [STAThread]
    static void Main(string[] args)
    {
      var indexer = new Indexer();
      indexer.Init();
      MainAsync(args, indexer).Wait();
      indexer.Save();
    }

    private async static Task MainAsync(string[] args, Indexer index)
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
        if (!await ProcessDirectory(new DirectoryInfo(args[2]), args[0] == "fix" ? ActionType.Fix : ActionType.Validate, args[1] == "unix" ? LineEnding.Unix : LineEnding.Windows, index))
        {
          result = false;
        }
      }
      if (!result)
      {
        Environment.Exit(-1);
      }
    }
    private static async Task<bool> ProcessDirectory(DirectoryInfo dirInfo, ActionType actionType, LineEnding lineEnding, Indexer index)
    {
      bool result = true;
      if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && !dirInfo.Name.IsValidFolder())
      {
        foreach (var d in dirInfo.GetDirectories())
        {
          if (!await ProcessDirectory(d, actionType, lineEnding, index))
          {
            result = false;
          }
        }
        foreach (var f in dirInfo.GetFiles())
        {
          if (!await ProcessFile(f, actionType, lineEnding, index))
          {
            result = false;
          }
        }
      }
      return result;
    }

    private static async Task<bool> ProcessFile(FileInfo fileInfo, ActionType actionType, LineEnding lineEnding, Indexer index)
    {
      if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && fileInfo.Extension.IsValidExtensionForProcessing())
      {
        var item = index.Get(fileInfo.FullName);
        if (item != null && item.Item1 == fileInfo.LastWriteTimeUtc && item.Item2 == true) return true;
        if (actionType == ActionType.Fix)
        {    
          return await FixFile(fileInfo, lineEnding, index);
        }
        else if (actionType == ActionType.Validate)
        {
          return await ValidateFile(fileInfo, lineEnding, index);
        }
      }
      return true;
    }

    private static async Task<bool> FixFile(FileInfo fileInfo, LineEnding lineEnding, Indexer index)
    {
      string content;
      using (var rdr = new StreamReader(fileInfo.FullName, Encoding.UTF8))
      {
        content = await rdr.ReadToEndAsync();
        rdr.Close();
      }
      if (content.Contains("\ufffd"))
      {
        using (var rdr = new StreamReader(fileInfo.FullName, Encoding.GetEncoding(1251)))
        {
          content = await rdr.ReadToEndAsync();
          rdr.Close();
        }
      }

      if (lineEnding == LineEnding.Unix)
      {
        content = WindowsLineEndingRx.Replace(content, "\n");
      }
      if (lineEnding == LineEnding.Windows)
      {
        content = UnixLineEndingRx.Replace(content, "$1\r\n");
      }
      File.Delete(fileInfo.FullName);
      using (var wrt = new StreamWriter(fileInfo.FullName, false, Encoding.UTF8))
      {
        await wrt.WriteAsync(content);
        wrt.Close();
      }
      index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(new FileInfo(fileInfo.FullName).LastWriteTimeUtc, true));
      return true;
    }

    private static async Task<bool> ValidateFile(FileInfo fileInfo, LineEnding lineEnding, Indexer index)
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
            index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, false));
            return false;
          }
        }
        else if (lineEnding == LineEnding.Windows)
        {
          if (UnixLineEndingRx.Match(content).Success)
          {
            Console.WriteLine($"Invalid line ending in file: {fileInfo.FullName}");
            index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, false));
            return false;
          }
        }
      }
      index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, true));
      return true;
    }
  }

  internal class Indexer
  {
    private const string IndexFileName = "index.bin";

    private Dictionary<string, Tuple<DateTime, bool>> _index;

    public Tuple<DateTime, bool> Get(string path)
    {
      if (_index == null) new InvalidOperationException("You should init indexer before usage. Call Init method");
      if (_index.ContainsKey(path))
      {
        return _index[path];
      }
      else
      {
        return null;
      }
    }

    public void Upsert(string path, Tuple<DateTime, bool> value)
    {
      if (_index == null) new InvalidOperationException("You should init indexer before usage. Call Init method");
      _index[path] = value;
    }

    public void Init()
    {
      if (File.Exists(IndexFileName))
      {
        using (var fs = File.OpenRead(IndexFileName))
        {
          var f = new BinaryFormatter();
          _index = (Dictionary<string, Tuple<DateTime, bool>>)f.Deserialize(fs);
          fs.Close();
        }
      }
      else
      {
        _index = new Dictionary<string, Tuple<DateTime, bool>>();
      }
    }
    public void Save()
    {
      if (File.Exists(IndexFileName))
      {
        File.Delete(IndexFileName);
      }
      using (var fs = File.OpenWrite(IndexFileName))
      {
        var f = new BinaryFormatter();
        f.Serialize(fs, _index);
        fs.Close();
      }
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
    private static List<string> _extensionList = string.IsNullOrEmpty(ConfigurationManager.AppSettings["ExtensionList"])
        ? new List<string> { ".cs", ".cshtml", ".txt", ".js", ".xml" }
        : new List<string>(ConfigurationManager.AppSettings["ExtensionList"].Split(';'));
    private static List<string> _excludeFolderList = string.IsNullOrEmpty(ConfigurationManager.AppSettings["ExcludeFolderList"])
        ? new List<string> { ".git", "bin" }
        : new List<string>(ConfigurationManager.AppSettings["ExcludeFolderList"].Split(';'));
    public static bool IsValidExtensionForProcessing(this string extension)
    {
      return _extensionList.Contains(extension);
    }
    public static bool IsValidFolder(this string extension)
    {
      return _excludeFolderList.Contains(extension);
    }
  }
}
