using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DafnyCore.Generic;
using DafnyCore.Options;
using Microsoft.Dafny;
using Microsoft.Dafny.LanguageServer.IntegrationTest.Util;
using Tomlyn;
using Tomlyn.Helpers;
using Tomlyn.Model;

namespace DafnyCore;

// Model class for the .doo file format for Dafny libraries.
// Contains the validation logic for safely consuming libraries as well.
public class DooFile {
  public const string Extension = ".doo";

  private const string ProgramFileEntry = "program";

  private const string ManifestFileEntry = "manifest.toml";

  public class ManifestData {
    public const string CurrentDooFileVersion = "1.0";
    public string DooFileVersion { get; set; }

    public string DafnyVersion { get; set; }

    public string SolverIdentifier { get; set; }
    public string SolverVersion { get; set; }

    public Dictionary<string, object> Options { get; set; }

    public ManifestData() {
      // Only for TOML deserialization!
    }

    public ManifestData(DafnyOptions options) {
      DooFileVersion = CurrentDooFileVersion;
      DafnyVersion = options.VersionNumber;

      SolverIdentifier = options.SolverIdentifier;
      // options.SolverVersion may be null (if --no-verify is used for example)
      SolverVersion = options.SolverVersion?.ToString();

      Options = new Dictionary<string, object>();
      foreach (var (option, _) in OptionChecks) {
        var optionValue = options.Get((dynamic)option);
        Options.Add(option.Name, optionValue);
      }
    }

    public static ManifestData Read(TextReader reader) {
      return Toml.ToModel<ManifestData>(reader.ReadToEnd(), null, new TomlModelOptions());
    }

    public void Write(TextWriter writer) {
      var content = Toml.FromModel(this, new TomlModelOptions() {
        ConvertToToml = obj => {
          if (obj is Enum) {
            TomlFormatHelper.ToString(obj.ToString()!, TomlPropertyDisplayKind.Default);
            return obj.ToString();
          }

          return obj;
        }
      }).Replace("\r\n", "\n");
      writer.Write(content);
    }
  }

  public ManifestData Manifest { get; set; }

  public string ProgramText { get; set; }

  public static async Task<DooFile> Read(string path) {
    using var archive = ZipFile.Open(path, ZipArchiveMode.Read);
    return await Read(archive);
  }

  public static async Task<DooFile> Read(Stream stream) {
    using var archive = new ZipArchive(stream);
    return await Read(archive);
  }

  private static async Task<DooFile> Read(ZipArchive archive) {
    var result = new DooFile();

    var manifestEntry = archive.GetEntry(ManifestFileEntry);
    if (manifestEntry == null) {
      throw new ArgumentException(".doo file missing manifest entry");
    }

    await using (var manifestStream = manifestEntry.Open()) {
      result.Manifest = ManifestData.Read(new StreamReader(manifestStream, Encoding.UTF8));
    }

    var programTextEntry = archive.GetEntry(ProgramFileEntry);
    if (programTextEntry == null) {
      throw new ArgumentException(".doo file missing program text entry");
    }

    await using (var programTextStream = programTextEntry.Open()) {
      var reader = new StreamReader(programTextStream, Encoding.UTF8);
      result.ProgramText = await reader.ReadToEndAsync();
    }

    return result;
  }

  public DooFile(Program dafnyProgram) {
    var tw = new StringWriter {
      NewLine = "\n"
    };
    var pr = new Printer(tw, dafnyProgram.Options, PrintModes.Serialization);
    // afterResolver is false because we don't yet have a way to safely skip resolution
    // when reading the program back into memory.
    // It's probably worth serializing a program in a more efficient way first
    // before adding that feature.
    pr.PrintProgram(dafnyProgram, false);
    ProgramText = tw.ToString();
    Manifest = new ManifestData(dafnyProgram.Options);
  }

  private DooFile() {
  }

  /// <summary>
  /// Returns the options as specified by the DooFile
  /// </summary>
  public DafnyOptions Validate(ErrorReporter reporter, string filePath, DafnyOptions options, IToken origin) {
    if (!options.UsingNewCli) {
      reporter.Error(MessageSource.Project, origin,
        $"cannot load {options.GetPrintPath(filePath)}: .doo files cannot be used with the legacy CLI");
      return null;
    }

    if (options.VersionNumber != Manifest.DafnyVersion) {
      reporter.Error(MessageSource.Project, origin,
        $"cannot load {options.GetPrintPath(filePath)}: it was built with Dafny {Manifest.DafnyVersion}, which cannot be used by Dafny {options.VersionNumber}");
      return null;
    }

    return CheckAndGetLibraryOptions(reporter, filePath, options, origin, Manifest.Options);
  }


  public static DafnyOptions CheckAndGetLibraryOptions(ErrorReporter reporter, string libraryFile,
    DafnyOptions options, IToken origin,
    Dictionary<string, object> libraryOptions) {
    var result = new DafnyOptions(options);
    var success = true;
    var relevantOptions = options.Options.OptionArguments.Keys.ToHashSet();
    foreach (var (option, check) in OptionChecks) {
      // It's important to only look at the options the current command uses,
      // because other options won't be initialized to the correct default value.
      // See CommandRegistry.Create().
      if (!relevantOptions.Contains(option)) {
        continue;
      }
      var localValue = options.Get(option);

      object libraryValue;
      if (libraryOptions.TryGetValue(option.Name, out var manifestValue)) {
        if (!TomlUtil.TryGetValueFromToml(reporter, origin, null,
              option.Name, option.ValueType, manifestValue, out libraryValue)) {
          return null;
        }
      } else {
        // This else can occur because Tomlyn will drop aggregate properties with no values.
        // When this happens, use the default value
        libraryValue = option.Parse("").GetValueForOption(option);
      }

      result.Options.OptionArguments[option] = libraryValue;
      result.ApplyBinding(option);
      var prefix = $"cannot load {options.GetPrintPath(libraryFile)}";
      success = success && check(reporter, origin, prefix, option, localValue, libraryValue);
    }

    if (!success) {
      return null;
    }

    return result;
  }

  public void Write(ConcreteSyntaxTree wr) {
    var manifestWr = wr.NewFile(ManifestFileEntry);
    using var manifestWriter = new StringWriter();
    Manifest.Write(manifestWriter);
    manifestWr.Write(manifestWriter.ToString().Replace("\r\n", "\n"));

    var programTextWr = wr.NewFile(ProgramFileEntry);
    programTextWr.Write(ProgramText);
  }

  public void Write(string path) {
    // Delete first, we don't want to merge with existing zips
    File.Delete(path);

    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

    var manifest = archive.CreateEntry(ManifestFileEntry);
    using (var manifestStream = manifest.Open()) {
      using var manifestWriter = new StreamWriter(manifestStream, Encoding.UTF8);
      Manifest.Write(manifestWriter);
    }

    var programText = archive.CreateEntry(ProgramFileEntry);
    using (var programTextStream = programText.Open()) {
      using var programTextWriter = new StreamWriter(programTextStream, Encoding.UTF8);
      programTextWriter.Write(ProgramText);
    }
  }

  // Partitioning of all options into subsets that must be recorded in a .doo file
  // to guard against unsafe usage.
  // Note that legacy CLI options are not as cleanly enumerated and therefore
  // more difficult to completely categorize, which is the main reason the LibraryBackend
  // is restricted to only the new CLI.

  private static readonly Dictionary<Option, OptionCompatibility.OptionCheck> OptionChecks = new();
  private static readonly HashSet<Option> NoChecksNeeded = new();

  public static void RegisterLibraryCheck(Option option, OptionCompatibility.OptionCheck check) {
    if (NoChecksNeeded.Contains(option)) {
      throw new ArgumentException($"Option already registered as not needing a library check: {option.Name}");
    }
    OptionChecks.Add(option, check);
  }

  public static void RegisterLibraryChecks(IDictionary<Option, OptionCompatibility.OptionCheck> checks) {
    foreach (var (option, check) in checks) {
      RegisterLibraryCheck(option, check);
    }
  }

  public static void RegisterNoChecksNeeded(Option option, bool semantic) {
    if (semantic) {
      RegisterLibraryCheck(option, OptionCompatibility.NoOpOptionCheck);
    } else {
      if (OptionChecks.ContainsKey(option)) {
        throw new ArgumentException($"Option already registered as needing a library check: {option.Name}");
      }
      NoChecksNeeded.Add(option);
    }
  }

  public static void CheckOptions(IEnumerable<Option> allOptions) {
    var unsupportedOptions = allOptions.ToHashSet()
      .Where(o =>
        !OptionChecks.ContainsKey(o) && !NoChecksNeeded.Contains(o))
      .ToList();
    if (unsupportedOptions.Any()) {
      throw new Exception($"Internal error - unsupported options registered: {{\n{string.Join(",\n", unsupportedOptions)}\n}}");
    }
  }
}
