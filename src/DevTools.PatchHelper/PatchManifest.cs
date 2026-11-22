using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DevTools.PatchHelper
{
    /// <summary>
    /// patch.json, written by DevTools Releases into every patch: what each module's files should become, with the hash
    /// each file has now (in the patch) and had before (on the server), plus the helper's title and the app pools and
    /// services to stop. Property names match the camelCase JSON DevTools writes.
    /// </summary>
    [DataContract]
    public sealed class PatchManifest
    {
        public const string FileName = "patch.json";

        [DataMember(Name = "formatVersion")] public int FormatVersion { get; set; }
        [DataMember(Name = "system")] public string System { get; set; } = "";
        [DataMember(Name = "title")] public string Title { get; set; } = "";
        [DataMember(Name = "subtitle")] public string? Subtitle { get; set; }
        [DataMember(Name = "supportContact")] public string? SupportContact { get; set; }
        [DataMember(Name = "patchName")] public string PatchName { get; set; } = "";
        [DataMember(Name = "description")] public string? Description { get; set; }
        [DataMember(Name = "createdAt")] public string? CreatedAt { get; set; }
        [DataMember(Name = "createdBy")] public string? CreatedBy { get; set; }
        [DataMember(Name = "modules")] public List<PatchManifestModule> Modules { get; set; } = new List<PatchManifestModule>();

        public static PatchManifest Load(string patchFolder)
        {
            var path = Path.Combine(patchFolder, FileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"{FileName} was not found next to the helper. Extract the whole patch to a folder first, then run the helper from that folder.", path);
            var manifest = Json.Read<PatchManifest>(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"{FileName} is empty.");
            if (manifest.FormatVersion != 1) throw new InvalidDataException($"This patch was made by a newer DevTools (format {manifest.FormatVersion}). Use the helper that came with it.");
            return manifest;
        }
    }

    [DataContract]
    public sealed class PatchManifestModule
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "clientFolder")] public string? ClientFolder { get; set; }
        [DataMember(Name = "appPool")] public string? AppPool { get; set; }
        [DataMember(Name = "service")] public string? Service { get; set; }
        [DataMember(Name = "files")] public List<PatchManifestFile> Files { get; set; } = new List<PatchManifestFile>();
    }

    [DataContract]
    public sealed class PatchManifestFile
    {
        public const string Added = "ADDED";
        public const string Changed = "CHANGED";
        public const string Deleted = "DELETED";

        /// <summary>Path inside the module folder, with forward slashes.</summary>
        [DataMember(Name = "path")] public string Path { get; set; } = "";
        [DataMember(Name = "kind")] public string Kind { get; set; } = "";
        [DataMember(Name = "size")] public long? Size { get; set; }
        /// <summary>Hash of the file in the patch (what the server should have afterwards); null for deleted files.</summary>
        [DataMember(Name = "sha256")] public string? Sha256 { get; set; }
        /// <summary>Hash the file had on the source's previous version; null for added files.</summary>
        [DataMember(Name = "previousSha256")] public string? PreviousSha256 { get; set; }
    }

    /// <summary>
    /// apply-state.json, kept next to the patch while it is being applied: which steps finished, where the backup is and
    /// which app pools and services were stopped. It lets the helper continue or roll back after it was closed mid-way.
    /// </summary>
    [DataContract]
    public sealed class ApplyState
    {
        public const string FileName = "apply-state.json";

        [DataMember(Name = "startedAt")] public string StartedAt { get; set; } = "";
        [DataMember(Name = "backupFolder")] public string BackupFolder { get; set; } = "";
        [DataMember(Name = "targets")] public Dictionary<string, string> Targets { get; set; } = new Dictionary<string, string>();
        [DataMember(Name = "completedSteps")] public List<string> CompletedSteps { get; set; } = new List<string>();
        [DataMember(Name = "stoppedAppPools")] public List<string> StoppedAppPools { get; set; } = new List<string>();
        [DataMember(Name = "stoppedServices")] public List<string> StoppedServices { get; set; } = new List<string>();
        /// <summary>applying, applied, failed or rolled-back.</summary>
        [DataMember(Name = "status")] public string Status { get; set; } = "applying";

        public bool IsUnfinished => Status == "applying" || Status == "failed";

        public static ApplyState? TryLoad(string patchFolder)
        {
            var path = Path.Combine(patchFolder, FileName);
            if (!File.Exists(path)) return null;
            try { return Json.Read<ApplyState>(File.ReadAllBytes(path)); }
            catch (Exception exception) when (exception is SerializationException || exception is IOException) { return null; }
        }

        public void Save(string patchFolder) => Json.WriteFile(Path.Combine(patchFolder, FileName), this);
    }

    /// <summary>One file the backup holds (or, for files that didn't exist before, the note to delete them on rollback).</summary>
    [DataContract]
    public sealed class BackupEntry
    {
        [DataMember(Name = "module")] public string Module { get; set; } = "";
        [DataMember(Name = "path")] public string Path { get; set; } = "";
        [DataMember(Name = "existed")] public bool Existed { get; set; }
    }

    [DataContract]
    public sealed class BackupIndex
    {
        public const string FileName = "backup.json";
        [DataMember(Name = "entries")] public List<BackupEntry> Entries { get; set; } = new List<BackupEntry>();
    }

    /// <summary>Remembered module folders per system, so the next patch doesn't ask again.</summary>
    [DataContract]
    public sealed class RememberedTargets
    {
        [DataMember(Name = "targets")] public Dictionary<string, string> Targets { get; set; } = new Dictionary<string, string>();
    }

    public static class Json
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        public static T? Read<T>(byte[] data) where T : class
        {
            using (var stream = new MemoryStream(data))
                return new DataContractJsonSerializer(typeof(T), Settings).ReadObject(stream) as T;
        }

        public static void WriteFile<T>(string path, T value)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, ownsStream: false, indent: true))
                new DataContractJsonSerializer(typeof(T), Settings).WriteObject(writer, value);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
