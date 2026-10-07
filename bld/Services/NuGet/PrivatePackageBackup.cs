using bld.Infrastructure;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Versioning;
using System.Xml.Linq;

namespace bld.Services.NuGet;

/// <summary>
/// Keeps the packages a repo restored from anywhere but nuget.org, so it still restores once the private
/// feed is out of reach and the NuGet caches are cleared. Each package is copied from the packages folder
/// restore extracted it into, into one folder feed per source, and nuget.offline.config points that
/// source's key at its folder. The source is the one restore recorded in the package's .nupkg.metadata:
/// nuget.config alone cannot tell, since a package may be on several feeds.
/// </summary>
internal sealed class PrivatePackageBackup(IConsoleOutput console) {
    public const string DefaultDirectoryName = ".nuget-private";
    public const string ConfigFileName = "nuget.offline.config";

    private const string NuGetOrgKey = "nuget.org";

    /// <param name="assetsFiles">project.assets.json of every project; missing ones are reported, not fatal.</param>
    /// <param name="settings">The nuget.config hierarchy of the root, for the source keys and the package source mapping.</param>
    public void Run(IEnumerable<string> assetsFiles, string backupDirectory, string configPath, ISettings settings) {
        backupDirectory = Path.GetFullPath(backupDirectory);
        var configured = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        var keyBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var derivedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<(string, string)>();
        var keptKeys = new List<string>();
        var kept = 0;
        var copied = 0;
        var missingAssets = new List<string>();
        var notCached = new List<string>();
        var noSource = new List<string>();
        var mirrored = new List<string>();

        foreach (var assetsFile in assetsFiles.Distinct(DirExt.PathComparer)) {
            if (!File.Exists(assetsFile)) {
                missingAssets.Add(assetsFile);
                continue;
            }
            var json = File.ReadAllText(assetsFile);
            var folders = ProjectAssetsReader.ParsePackageFolders(json);

            foreach (var package in ProjectAssetsReader.Parse(json)) {
                if (!NuGetVersion.TryParse(package.Version, out var version)) continue;
                if (!seen.Add((package.Id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant()))) continue;

                var name = $"{package.Id} {version.ToNormalizedString()}";
                if (Locate(folders, package.Id, version) is not { } cached) {
                    notCached.Add(name);
                    continue;
                }
                var (nupkg, source) = cached;
                if (string.IsNullOrEmpty(source)) {
                    noSource.Add(name);
                    continue;
                }
                if (IsPublic(source)) continue;
                if (IsSignedByNuGetOrg(nupkg)) {
                    mirrored.Add(name);
                    continue;
                }

                var key = KeyFor(source);
                if (!keptKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) keptKeys.Add(key);
                kept++;

                // Restored from the folder feed itself, through a nuget.offline.config written earlier.
                if (IsUnder(source, backupDirectory)) continue;

                var target = Path.Combine(backupDirectory, FolderName(key), Path.GetFileName(nupkg));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(nupkg).Length) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(nupkg, target, overwrite: true);
                copied++;
            }
        }

        if (missingAssets.Count > 0) {
            console.WriteWarning($"{missingAssets.Count} project(s) have no {ProjectAssetsReader.FileName}, so their packages are not kept; run dotnet restore first.");
            foreach (var file in missingAssets) console.WriteDebug($"  missing {file}");
        }
        if (notCached.Count > 0) {
            console.WriteWarning($"{notCached.Count} package(s) are not in the packages folder and cannot be kept: {string.Join(", ", notCached)}");
        }
        if (noSource.Count > 0) {
            console.WriteWarning($"{noSource.Count} package(s) have no source recorded (restored by an older NuGet), so bld cannot tell whether they are private; not kept: {string.Join(", ", noSource)}");
        }

        if (mirrored.Count > 0) {
            console.WriteLine($"Not kept, {mirrored.Count} package(s) came through a private source but are nuget.org's own files (repository-signed by nuget.org).");
            foreach (var name in mirrored) console.WriteDebug($"  from nuget.org: {name}");
        }

        var dropped = WriteConfig(configPath, backupDirectory, configured, settings);

        console.WriteLine(kept == 0
            ? $"No private packages found; nothing kept in {backupDirectory}."
            : $"Kept {kept} private package(s) from {string.Join(", ", keptKeys)} in {backupDirectory} ({copied} copied now).");
        if (dropped.Count > 0) {
            console.WriteLine($"Left out of {ConfigFileName}, nothing was restored from them: {string.Join(", ", dropped)}");
        }
        console.WriteLine($"Wrote {configPath}. Restore without the private feeds: dotnet restore --configfile {Path.GetFileName(configPath)}");

        string KeyFor(string source) {
            if (keyBySource.TryGetValue(source, out var key)) return key;

            if (IsUnder(source, backupDirectory)) {
                key = Path.GetRelativePath(backupDirectory, Path.GetFullPath(source)).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            }
            else if (configured.FirstOrDefault(c => SameSource(c.Source, source)) is { } match) {
                key = match.Name;
            }
            else {
                // Restored from a source no nuget.config here names (a --source, a config since removed).
                var baseKey = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Host : Path.GetFileName(source.TrimEnd('/', '\\'));
                key = baseKey;
                for (var i = 2; derivedKeys.Contains(key) || configured.Any(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)); i++) {
                    key = $"{baseKey}-{i}";
                }
                derivedKeys.Add(key);
            }
            return keyBySource[source] = key;
        }
    }

    /// <summary>
    /// A clear source list: nuget.org as configured, every other configured source that has a folder in
    /// <paramref name="backupDirectory"/> pointing at it under its own key (so package source mapping keeps
    /// working), and the folders no configured source claims. Credentials are not carried over. Returns the
    /// configured sources left out.
    /// </summary>
    private static List<string> WriteConfig(string configPath, string backupDirectory, List<PackageSource> configured, ISettings settings) {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var folders = Directory.Exists(backupDirectory)
            ? Directory.GetDirectories(backupDirectory).Where(d => Directory.EnumerateFiles(d, "*.nupkg").Any()).ToList()
            : new List<string>();
        // Source keys are case-insensitive, folder names on Linux are not: two folders differing only in
        // case would be one key, so the first one wins instead of ToDictionary throwing.
        var folderByKey = folders
            .OrderBy(f => f, StringComparer.Ordinal)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sources = new XElement("packageSources", new XElement("clear"));
        var written = new List<(string Key, string? Folder)>();
        var dropped = new List<string>();

        foreach (var source in configured) {
            if (IsNuGetOrg(source.Source)) {
                sources.Add(Add(source.Name, source.Source));
                written.Add((source.Name, null));
            }
            else if (folderByKey.Remove(FolderName(source.Name), out var folder)) {
                sources.Add(Add(source.Name, FeedPath(folder)));
                written.Add((source.Name, folder));
            }
            else {
                dropped.Add(source.Name);
            }
        }
        foreach (var (key, folder) in folderByKey.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) {
            sources.Add(Add(key, FeedPath(folder)));
            written.Add((key, folder));
        }
        // Whatever the repo adds later comes from nuget.org, the one feed that stays reachable.
        if (!written.Any(w => w.Folder is null)) {
            sources.Add(Add(NuGetOrgKey, NuGetConstants.V3FeedUrl));
            written.Add((NuGetOrgKey, null));
        }

        var configuration = new XElement("configuration", sources);

        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        if (mapping.IsEnabled) {
            // The configured patterns, plus every kept package by id: a folder no configured source
            // claims has no patterns otherwise, and mapping would keep restore away from it.
            var items = new PackageSourceMappingProvider(settings).GetPackageSourceMappingItems();
            var mapped = new XElement("packageSourceMapping", new XElement("clear"));
            foreach (var (key, folder) in written) {
                var patterns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.Where(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))) {
                    patterns.UnionWith(item.Patterns.Select(p => p.Pattern));
                }
                if (folder is not null) patterns.UnionWith(PackageIds(folder));
                if (patterns.Count == 0) continue;
                mapped.Add(new XElement("packageSource", new XAttribute("key", key),
                    patterns.Select(p => new XElement("package", new XAttribute("pattern", p)))));
            }
            configuration.Add(mapped);
        }

        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            // No "--configfile" here: XML comments cannot contain a double hyphen.
            new XComment($" Private sources point at the packages kept in {FeedPath(backupDirectory)}. Pass this file to dotnet restore as its configfile to restore without the private feeds. "),
            configuration).Save(configPath);
        return dropped;

        // Relative to the config when the folder sits below it, so the repo can move as a whole.
        string FeedPath(string folder) {
            var relative = Path.GetRelativePath(configDirectory, folder);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? folder
                : relative.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static XElement Add(string key, string value) {
        var add = new XElement("add", new XAttribute("key", key), new XAttribute("value", value));
        if (value.EndsWith("index.json", StringComparison.OrdinalIgnoreCase)) add.Add(new XAttribute("protocolVersion", "3"));
        return add;
    }

    private static IEnumerable<string> PackageIds(string folder) {
        foreach (var file in Directory.EnumerateFiles(folder, "*.nupkg")) {
            string? id = null;
            try {
                using var reader = new PackageArchiveReader(file);
                id = reader.NuspecReader.GetId();
            }
            catch (Exception) {
                // Not a readable package; nothing to map.
            }
            if (id is not null) yield return id;
        }
    }

    /// <summary>The .nupkg and the source restore recorded for it, from the first packages folder holding it.</summary>
    internal static (string Nupkg, string? Source)? Locate(IReadOnlyList<string> packageFolders, string id, NuGetVersion version) {
        foreach (var folder in packageFolders) {
            var resolver = new VersionFolderPathResolver(folder);
            var nupkg = resolver.GetPackageFilePath(id, version);
            if (!File.Exists(nupkg)) continue;
            var metadata = resolver.GetNupkgMetadataPath(id, version);
            return (nupkg, File.Exists(metadata) ? NupkgMetadataFileFormat.Read(metadata).Source : null);
        }
        return null;
    }

    /// <summary>
    /// nuget.org, and the folders the SDK and Visual Studio ship packages in: all of them stay available
    /// without anyone's credentials.
    /// </summary>
    internal static bool IsPublic(string source) {
        if (IsNuGetOrg(source)) return true;
        if (IsHttp(source)) return false;
        var normalized = source.Replace('\\', '/');
        return normalized.Contains("/library-packs", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Microsoft SDKs/NuGetPackages", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the file carries nuget.org's repository signature, as a primary signature or as the
    /// countersignature on an author-signed package. nuget.org signs every package it serves, and a
    /// mirror (Artifactory remote, Azure Artifacts upstream) passes the file through unchanged, so this
    /// exact file stays restorable from nuget.org. A package a feed republished under the same id and
    /// version is a different file without that signature, and is kept. Nothing is verified here: the
    /// question is where the file came from, not whether to trust it, and restore verifies anyway.
    /// </summary>
    internal static bool IsSignedByNuGetOrg(string nupkg) {
        try {
            using var reader = new PackageArchiveReader(nupkg);
            var signature = reader.GetPrimarySignatureAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (signature is null) return false;
            IRepositorySignature? repository = signature as RepositoryPrimarySignature ?? (IRepositorySignature?)RepositoryCountersignature.GetRepositoryCountersignature(signature);
            return repository?.V3ServiceIndexUrl is { } url && IsNuGetOrg(url.AbsoluteUri);
        }
        catch (Exception) {
            // Unreadable or malformed signature: not provably nuget.org's, so keep the package.
            return false;
        }
    }

    internal static bool IsNuGetOrg(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && (uri.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.nuget.org", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttp(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static bool SameSource(string a, string b) =>
        IsHttp(a) || IsHttp(b)
            ? string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            : DirExt.PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));

    private static bool IsUnder(string source, string directory) {
        if (IsHttp(source)) return false;
        var relative = Path.GetRelativePath(directory, Path.GetFullPath(source));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>A source key as a directory name; keys may hold anything, directory names may not.</summary>
    internal static string FolderName(string key) =>
        string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c));
}
