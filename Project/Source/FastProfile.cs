using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Verse;

namespace FastLoader
{
    internal sealed class ProfileScope : IDisposable
    {
        private readonly ProfileNode node;
        private readonly Stopwatch stopwatch;
        private bool disposed;

        public ProfileScope(ProfileNode node)
        {
            this.node = node;
            stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopwatch.Stop();
            FastProfile.FinishScope(node, stopwatch.Elapsed);
        }
    }

    internal sealed class ProfileNode
    {
        public int Sequence;
        public string Name;
        public TimeSpan Elapsed;
        public ProfileNode Parent;
        public readonly List<ProfileNode> Children = new List<ProfileNode>();
    }

    internal sealed class ProfileSummary
    {
        public int Count;
        public TimeSpan Total;
        public TimeSpan Max;
    }

    internal static class FastProfile
    {
        private static readonly bool ProfilingEnabled = true;
        private const string ProjectLogDirectory = @"D:\GitProject\RimworldAnalyzeProj\StartupProfilerLogs";

        private static readonly object LockObject = new object();
        private static Stopwatch totalStopwatch;
        private static ProfileNode rootNode;
        private static string status;
        private static string reason;
        private static string inputHash;
        private static int nextSequence;

        [ThreadStatic]
        private static ProfileNode currentNode;

        public static void Begin()
        {
            if (!ProfilingEnabled)
            {
                return;
            }

            lock (LockObject)
            {
                nextSequence = 0;
                rootNode = new ProfileNode
                {
                    Sequence = nextSequence++,
                    Name = "FastLoader load total"
                };
                currentNode = rootNode;
                totalStopwatch = Stopwatch.StartNew();
                status = "UNKNOWN";
                reason = string.Empty;
                inputHash = string.Empty;
            }
        }

        public static ProfileScope Scope(string name)
        {
            if (!ProfilingEnabled)
            {
                return null;
            }

            lock (LockObject)
            {
                if (rootNode == null)
                {
                    return null;
                }

                ProfileNode parent = currentNode ?? rootNode;
                ProfileNode node = new ProfileNode
                {
                    Sequence = nextSequence++,
                    Name = name ?? "(unnamed)",
                    Parent = parent
                };
                parent.Children.Add(node);
                currentNode = node;
                return new ProfileScope(node);
            }
        }

        public static void FinishScope(ProfileNode node, TimeSpan elapsed)
        {
            if (!ProfilingEnabled || node == null)
            {
                return;
            }

            lock (LockObject)
            {
                node.Elapsed = elapsed;
                if (currentNode == node)
                {
                    currentNode = node.Parent;
                }
                else if (node.Parent != null)
                {
                    currentNode = node.Parent;
                }
            }
        }

        public static void SetStatus(string newStatus, string newReason, string newInputHash)
        {
            if (!ProfilingEnabled)
            {
                return;
            }

            lock (LockObject)
            {
                status = newStatus ?? status;
                reason = newReason ?? reason;
                if (!string.IsNullOrEmpty(newInputHash))
                {
                    inputHash = newInputHash;
                }
            }
        }

        public static void End(string finalStatus, string finalReason, string finalInputHash, int parsedDefCount)
        {
            if (!ProfilingEnabled)
            {
                return;
            }

            TimeSpan total;
            string finalStatusText;
            string finalReasonText;
            string finalInputHashText;
            ProfileNode rootSnapshot;

            lock (LockObject)
            {
                if (totalStopwatch == null || rootNode == null)
                {
                    return;
                }

                totalStopwatch.Stop();
                total = totalStopwatch.Elapsed;
                rootNode.Elapsed = total;
                finalStatusText = finalStatus ?? status ?? "UNKNOWN";
                finalReasonText = finalReason ?? reason ?? string.Empty;
                finalInputHashText = finalInputHash ?? inputHash ?? string.Empty;
                rootSnapshot = CloneTree(rootNode, null);
                totalStopwatch = null;
                rootNode = null;
                currentNode = null;
            }

            try
            {
                Directory.CreateDirectory(ProjectLogDirectory);
                string profilePath = CreateUniqueLogPath(ProjectLogDirectory, "fastloader_cache_profile", ".md");
                string text = BuildLog(finalStatusText, finalReasonText, finalInputHashText, parsedDefCount, total, rootSnapshot);
                File.WriteAllText(profilePath, text, new UTF8Encoding(false));
                Log.Message(string.Format("[FastLoader] cache {0}, defs={1}, total={2}, reason={3}, profile={4}",
                    finalStatusText,
                    parsedDefCount,
                    FormatDuration(total),
                    finalReasonText,
                    profilePath));
            }
            catch (Exception ex)
            {
                Log.Warning("[FastLoader] Failed to write profile log.\n" + ex);
            }
        }

        private static ProfileNode CloneTree(ProfileNode source, ProfileNode parent)
        {
            ProfileNode clone = new ProfileNode
            {
                Sequence = source.Sequence,
                Name = source.Name,
                Elapsed = source.Elapsed,
                Parent = parent
            };

            for (int i = 0; i < source.Children.Count; i++)
            {
                clone.Children.Add(CloneTree(source.Children[i], clone));
            }

            return clone;
        }

        private static string BuildLog(string finalStatus, string finalReason, string finalInputHash, int parsedDefCount, TimeSpan total, ProfileNode root)
        {
            List<ProfileNode> nodes = new List<ProfileNode>();
            CollectNodes(root, nodes);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# FastLoader cache profile");
            builder.AppendLine();
            builder.AppendLine("metadata:");
            builder.AppendLine("| key | value |");
            builder.AppendLine("|---|---|");
            builder.AppendLine("| createdLocal | " + EscapeTableCell(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)) + " |");
            builder.AppendLine("| status | " + EscapeTableCell(finalStatus) + " |");
            builder.AppendLine("| reason | " + EscapeTableCell(finalReason) + " |");
            builder.AppendLine("| inputHash | " + EscapeTableCell(finalInputHash) + " |");
            builder.AppendLine("| parsedDefCount | " + parsedDefCount.ToString(CultureInfo.InvariantCulture) + " |");
            builder.AppendLine("| total | " + FormatDuration(total) + " |");
            builder.AppendLine();

            AppendTree(builder, root);
            AppendSlowestScopes(builder, nodes);
            AppendAggregatedScopes(builder, nodes);

            return builder.ToString();
        }

        private static void AppendTree(StringBuilder builder, ProfileNode root)
        {
            builder.AppendLine("executionTree:");
            builder.AppendLine("```text");
            builder.AppendLine(root.Name + ": " + FormatDuration(root.Elapsed));
            for (int i = 0; i < root.Children.Count; i++)
            {
                AppendTreeNode(builder, root.Children[i], string.Empty, i == root.Children.Count - 1);
            }
            builder.AppendLine("```");
        }

        private static void AppendTreeNode(StringBuilder builder, ProfileNode node, string prefix, bool isLast)
        {
            builder.Append(prefix);
            builder.Append(isLast ? "└─ " : "├─ ");
            builder.Append(node.Name);
            builder.Append(": ");
            builder.AppendLine(FormatDuration(node.Elapsed));

            string childPrefix = prefix + (isLast ? "   " : "│  ");
            for (int i = 0; i < node.Children.Count; i++)
            {
                AppendTreeNode(builder, node.Children[i], childPrefix, i == node.Children.Count - 1);
            }
        }

        private static void AppendSlowestScopes(StringBuilder builder, List<ProfileNode> nodes)
        {
            List<ProfileNode> ordered = new List<ProfileNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Parent != null)
                {
                    ordered.Add(nodes[i]);
                }
            }

            ordered.Sort((left, right) => right.Elapsed.CompareTo(left.Elapsed));

            builder.AppendLine();
            builder.AppendLine("slowestScopes:");
            builder.AppendLine("| no | elapsed | scope |");
            builder.AppendLine("|---:|---:|---|");
            int limit = Math.Min(ordered.Count, 40);
            for (int i = 0; i < limit; i++)
            {
                ProfileNode node = ordered[i];
                builder.Append("| ");
                builder.Append((i + 1).ToString(CultureInfo.InvariantCulture));
                builder.Append(" | ");
                builder.Append(FormatDuration(node.Elapsed));
                builder.Append(" | ");
                builder.Append(EscapeTableCell(BuildPath(node)));
                builder.AppendLine(" |");
            }
        }

        private static void AppendAggregatedScopes(StringBuilder builder, List<ProfileNode> nodes)
        {
            Dictionary<string, ProfileSummary> summaries = new Dictionary<string, ProfileSummary>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                ProfileNode node = nodes[i];
                if (node.Parent == null)
                {
                    continue;
                }

                ProfileSummary summary;
                if (!summaries.TryGetValue(node.Name, out summary))
                {
                    summary = new ProfileSummary();
                    summaries.Add(node.Name, summary);
                }

                summary.Count++;
                summary.Total += node.Elapsed;
                if (node.Elapsed > summary.Max)
                {
                    summary.Max = node.Elapsed;
                }
            }

            List<KeyValuePair<string, ProfileSummary>> ordered = new List<KeyValuePair<string, ProfileSummary>>(summaries);
            ordered.Sort((left, right) => right.Value.Total.CompareTo(left.Value.Total));

            builder.AppendLine();
            builder.AppendLine("aggregatedScopes:");
            builder.AppendLine("| no | total | max | count | scope |");
            builder.AppendLine("|---:|---:|---:|---:|---|");
            for (int i = 0; i < ordered.Count; i++)
            {
                KeyValuePair<string, ProfileSummary> item = ordered[i];
                builder.Append("| ");
                builder.Append((i + 1).ToString(CultureInfo.InvariantCulture));
                builder.Append(" | ");
                builder.Append(FormatDuration(item.Value.Total));
                builder.Append(" | ");
                builder.Append(FormatDuration(item.Value.Max));
                builder.Append(" | ");
                builder.Append(item.Value.Count.ToString(CultureInfo.InvariantCulture));
                builder.Append(" | ");
                builder.Append(EscapeTableCell(item.Key));
                builder.AppendLine(" |");
            }
        }

        private static void CollectNodes(ProfileNode node, List<ProfileNode> nodes)
        {
            if (node == null)
            {
                return;
            }

            nodes.Add(node);
            for (int i = 0; i < node.Children.Count; i++)
            {
                CollectNodes(node.Children[i], nodes);
            }
        }

        private static string BuildPath(ProfileNode node)
        {
            List<string> parts = new List<string>();
            ProfileNode current = node;
            while (current != null)
            {
                parts.Add(current.Name);
                current = current.Parent;
            }

            parts.Reverse();
            return string.Join(" > ", parts.ToArray());
        }

        private static string CreateUniqueLogPath(string directory, string prefix, string extension)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, prefix + "_" + timestamp + extension);
            int suffix = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(directory, prefix + "_" + timestamp + "_" + suffix.ToString(CultureInfo.InvariantCulture) + extension);
                suffix++;
            }

            return path;
        }

        private static string EscapeTableCell(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string FormatDuration(TimeSpan elapsed)
        {
            double seconds = elapsed.Seconds + (elapsed.Milliseconds / 1000.0);
            if (elapsed.TotalHours >= 1.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}시간 {1}분 {2:0.00}초", (int)elapsed.TotalHours, elapsed.Minutes, seconds);
            }

            if (elapsed.TotalMinutes >= 1.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}분 {1:0.00}초", (int)elapsed.TotalMinutes, seconds);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.00}초", elapsed.TotalSeconds);
        }
    }
}
