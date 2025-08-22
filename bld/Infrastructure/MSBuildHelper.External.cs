using System.Diagnostics;

namespace bld.Infrastructure;

// contains code from https://github.com/KirillOsenkov/MSBuildStructuredLog/blob/main/src/StructuredLogViewer/MSBuildLocator.cs
/*
The MIT License (MIT)

Copyright (c) 2016 Kirill Osenkov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

internal static partial class MSBuildHelper {

#if FALSE
    public static IEnumerable<MsbuildLocation> GetPreferredLocations(bool prefer64bit = true) {
        string[] vs15Locations = GetVS15Locations();
        if (vs15Locations != null && vs15Locations.Length > 0) {
            HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (prefer64bit) {
                hashSet.UnionWith(vs15Locations.Select((string l) => Path.Combine(l, "MSBuild", "15.0", "Bin", "amd64", "MSBuild.exe")));
                hashSet.UnionWith(vs15Locations.Select((string l) => Path.Combine(l, "MSBuild", "Current", "Bin", "amd64", "MSBuild.exe")));
            }
            else {
                hashSet.UnionWith(vs15Locations.Select((string l) => Path.Combine(l, "MSBuild", "15.0", "Bin", "MSBuild.exe")));
                hashSet.UnionWith(vs15Locations.Select((string l) => Path.Combine(l, "MSBuild", "Current", "Bin", "MSBuild.exe")));
            }

            var msbuildLoc = hashSet
                .Where(path => Path.Exists(path))
                .OrderByDescending(path => path)
                .Select(path => new MsbuildLocation(path, MsbuildExecType.ExecTypeExe, MsbuildLocationType.VisualStudio))
                .FirstOrDefault();

            if (msbuildLoc is not null) yield return msbuildLoc;
        }

        MsbuildLocation Translate(VisualStudioInstance instance) {
            if (instance is null) return default;

            var isFile = File.Exists(instance.MSBuildPath);
            if (!isFile) {
                if (File.Exists(Path.Combine(instance.MSBuildPath, "MSBuild.exe"))) {
                    return new MsbuildLocation(Path.Combine(instance.MSBuildPath, "MSBuild.exe"), MsbuildExecType.ExecTypeExe, MsbuildLocationType.VisualStudio);
                }
                else if (File.Exists(Path.Combine(instance.MSBuildPath, "MSBuild.dll"))) {
                    return new MsbuildLocation(Path.Combine(instance.MSBuildPath, "MSBuild.dll"), MsbuildExecType.ExecTypeDllDotnet, MsbuildLocationType.DotNetSdk);
                }
            }
            else {
                var ext = Path.GetExtension(instance.MSBuildPath);
                if (ext is null) {
                    return null;
                }
                else {
                    if (0 == string.Compare(".exe", ext, StringComparison.OrdinalIgnoreCase)) new MsbuildLocation(instance.MSBuildPath, MsbuildExecType.ExecTypeExe, MsbuildLocationType.VisualStudio);
                    if (0 == string.Compare(".dll", ext, StringComparison.OrdinalIgnoreCase)) new MsbuildLocation(instance.MSBuildPath, MsbuildExecType.ExecTypeDllDotnet, MsbuildLocationType.DotNetSdk);
                }
            }
            return null;
        }

        var msbuildLoc2 = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions { DiscoveryTypes = DiscoveryType.DotNetSdk })
            .OrderByDescending(inst => inst.Version)
            .Select(Translate)
            .FirstOrDefault();

        if (msbuildLoc2 is not null) yield return msbuildLoc2;
    }
#endif
    internal static string[] GetVS15Locations() {
        string vswhere = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer"), "vswhere.exe");
        if (!File.Exists(vswhere)) {
            return Array.Empty<string>();
        }
        string args = "-prerelease -format value -property installationPath -nologo";
        var startInfo = new ProcessStartInfo {
            Arguments = args,
            CreateNoWindow = true,
            FileName = vswhere,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        List<string> resultList = new List<string>();
        using var process = Process.Start(startInfo);
        if (process is null) {
            return Array.Empty<string>();
        }
        string output = process.StandardOutput.ReadToEnd();
        resultList.AddRange(output.GetLines().Where(Directory.Exists));
        process.WaitForExit(3000);
        if (process.ExitCode != 0) {
            return Array.Empty<string>();
        }
        return resultList.ToArray();
    }

    public static IReadOnlyList<string> GetLines(this string text, bool includeLineBreak = false) {
        return (from span in text.GetLineSpans(includeLineBreak)
                select text.Substring(span.Start, span.Length)).ToArray();
    }

    public static IReadOnlyList<Span> GetLineSpans(this string text, bool includeLineBreakInSpan = true) {
        if (text == null) {
            throw new ArgumentNullException("text");
        }
        if (text.Length == 0) {
            return Empty;
        }
        List<Span> result = new List<Span>();
        text.CollectLineSpans(result, includeLineBreakInSpan);
        return result.ToArray();
    }

    public static void CollectLineSpans(this string text, ICollection<Span> spans, bool includeLineBreakInSpan = true) {
        if (text == null) {
            throw new ArgumentNullException("text");
        }
        if (spans == null) {
            throw new ArgumentNullException("spans");
        }
        if (text.Length == 0) {
            return;
        }
        int currentPosition = 0;
        int currentLineLength = 0;
        bool previousWasCarriageReturn = false;
        for (int i = 0; i < text.Length; i++) {
            switch (text[i]) {
                case '\r':
                    if (previousWasCarriageReturn) {
                        int lineLengthIncludingLineBreak = currentLineLength;
                        if (!includeLineBreakInSpan) {
                            currentLineLength--;
                        }
                        spans.Add(new Span(currentPosition, currentLineLength));
                        currentPosition += lineLengthIncludingLineBreak;
                        currentLineLength = 1;
                    }
                    else {
                        currentLineLength++;
                        previousWasCarriageReturn = true;
                    }
                    continue;
                case '\n': {
                    int lineLength2 = currentLineLength;
                    if (previousWasCarriageReturn) {
                        lineLength2--;
                    }
                    currentLineLength++;
                    previousWasCarriageReturn = false;
                    if (includeLineBreakInSpan) {
                        lineLength2 = currentLineLength;
                    }
                    spans.Add(new Span(currentPosition, lineLength2));
                    currentPosition += currentLineLength;
                    currentLineLength = 0;
                    continue;
                }
            }
            if (previousWasCarriageReturn) {
                int lineLength = currentLineLength;
                if (!includeLineBreakInSpan) {
                    lineLength--;
                }
                spans.Add(new Span(currentPosition, lineLength));
                currentPosition += currentLineLength;
                currentLineLength = 0;
                previousWasCarriageReturn = false;
            }
            currentLineLength++;
        }
        int finalLength = currentLineLength;
        if (previousWasCarriageReturn && !includeLineBreakInSpan) {
            finalLength--;
        }
        spans.Add(new Span(currentPosition, finalLength));
        if (previousWasCarriageReturn) {
            spans.Add(new Span(currentPosition, 0));
        }
    }
    public struct Span {
        public int Start;

        public int Length;

        public static readonly Span Empty = new Span(0, 0);

        public int End => Start + Length;

        public Span(int start, int length) {
            this = default(Span);
            Start = start;
            Length = length;
        }

        public override string ToString() {
            return $"({Start}, {Length})";
        }

        public Span Skip(int length) {
            if (length > Length) {
                return default(Span);
            }
            return new Span(Start + length, Length - length);
        }

        public bool Contains(int position) {
            if (position >= Start) {
                return position <= End;
            }
            return false;
        }
    }

    private static readonly IReadOnlyList<Span> Empty = new Span[1] { Span.Empty };
}
