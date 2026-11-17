using System;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using BDInfo.IO;
using DiscUtils.Udf;

namespace MediaBrowser.MediaEncoding.BdInfo;

/// <summary>Read-only BDInfo access to files inside an image; the caller owns the reader.</summary>
internal sealed class UdfDirectoryInfo(UdfReader reader, string path) : IDirectoryInfo
{
    public string Name => Path.GetFileName(FullName);

    public string FullName => path.Replace('\\', '/').Trim('/');

    public IDirectoryInfo? Parent => string.IsNullOrEmpty(path) ? null : new UdfDirectoryInfo(reader, Path.GetDirectoryName(FullName) ?? string.Empty);

    public IDirectoryInfo[] GetDirectories() => reader.GetDirectories(FullName.Replace('/', '\\')).Select(p => (IDirectoryInfo)new UdfDirectoryInfo(reader, p)).ToArray();

    public IFileInfo[] GetFiles() => reader.GetFiles(FullName.Replace('/', '\\')).Select(p => (IFileInfo)new UdfFileInfo(reader, p)).ToArray();

    public IFileInfo[] GetFiles(string searchPattern) => GetFiles(searchPattern, SearchOption.TopDirectoryOnly);

    public IFileInfo[] GetFiles(string searchPattern, SearchOption searchOption)
        // BDInfo passes bare extensions (".mpls"), not filesystem wildcards.
        => reader.GetFiles(FullName.Replace('/', '\\'), "*.*", searchOption)
            .Where(p => searchPattern.StartsWith('.')
                ? string.Equals(Path.GetExtension(p), searchPattern, StringComparison.OrdinalIgnoreCase)
                : FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(p.Replace('\\', '/')), true))
            .Select(p => (IFileInfo)new UdfFileInfo(reader, p)).ToArray();

    private sealed class UdfFileInfo(UdfReader reader, string path) : IFileInfo
    {
        public string Name => Path.GetFileName(FullName);

        public string FullName => path.Replace('\\', '/');

        public string Extension => Path.GetExtension(path);

        public long Length => reader.GetFileLength(path);

        public bool IsDir => false;

        public Stream OpenRead() => reader.OpenFile(path, FileMode.Open, FileAccess.Read);

        public StreamReader OpenText() => new(OpenRead());
    }
}
