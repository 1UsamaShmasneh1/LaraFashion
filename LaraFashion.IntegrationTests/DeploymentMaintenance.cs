using LaraFashion.Data;
using System.Text.Json;

sealed class MaintenanceResult
{
    public int ContractVersion { get; set; } = 1;
    public string CandidateCommit { get; set; } = DeploymentDatabase.CandidateCommit;
    public string Command { get; set; } = "";
    public bool Success { get; set; }
    public string FailureCode { get; set; } = "";
    public Dictionary<string,bool> Gates { get; set; } = [];
    public string SchemaState { get; set; } = "";
    public int ExpectedEvidenceCount { get; set; }
    public string ExpectedEvidenceDigest { get; set; } = "";
    public int PreparedEvidenceCount { get; set; }
    public string PreparedEvidenceDigest { get; set; } = "";
    public int ImportedEvidenceCount { get; set; }
    public string ImportedEvidenceDigest { get; set; } = "";
    public int SourceLines { get; set; }
    public int ChangedSourceLines { get; set; }
    public int MissingSourceLines { get; set; }
    public int AlreadyIdentifiedSourceLines { get; set; }
    public int UnknownNotInSource { get; set; }
    public int BusinessTablesCompared { get; set; }
    public int PreviousTriggersPreserved { get; set; }
    public string SnapshotSha256 { get; set; } = "";
}
sealed class DeploymentAttempt
{
    public int ContractVersion { get; set; } = 1;
    public string Commit { get; set; } = "";
    public string PreviousRelease { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string EvidenceSourcePath { get; set; } = "";
    public string EvidenceSourceSha256 { get; set; } = "";
    public string TargetIdentity { get; set; } = "";
    public string Stage { get; set; } = "";
    public bool StopAttempted { get; set; }
    public bool WritersStopped { get; set; }
    public bool DatabaseChangeAttempted { get; set; }
    public bool FilesSwapAttempted { get; set; }
    public bool ApplicationStartAttempted { get; set; }
    public bool DatabaseRestoreAttempted { get; set; }
    public string BeforeRestorePath { get; set; } = "";
    public string BeforeRestoreSha256 { get; set; } = "";
    public List<string> Stages { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }
}
sealed record ReleaseManifest(int ContractVersion,string Commit,string Migration,string ApplicationSha256);
static class DeploymentMaintenance
{
    static readonly Dictionary<string,string> RequiredGates = new()
    {
        ["--maintenance-contract"]="ToolContractVerified", ["--deployment-backup"]="BackupVerified",
        ["--deployment-plan"]="SchemaClassified", ["--deployment-freeze"]="TargetFrozen",
        ["--verify-review-upgrade"]="RehearsalVerified", ["--prepare-legacy-review"]="EvidenceVerified",
        ["--deployment-apply"]="SchemaVerified", ["--deployment-verify"]="BusinessPreserved",
        ["--deployment-restore"]="RestoreVerified", ["--deployment-health"]="HealthVerified",
        ["--deployment-recovery-health"]="RecoveryHealthVerified", ["--deployment-dp-probe"]="ExistingKeysCompatibleAndUnchanged",
        ["--deployment-state"]="StateRecorded", ["--deployment-compatible"]="CompatibleReleaseVerified",
        ["--deployment-recovery-inspect"]="RecoveryInspected", ["--deployment-package"]="PackageVerified", ["--deployment-previous-attempt"]="PreviousAttemptResolved", ["--deployment-config"]="RuntimeConfigVerified",["--deployment-manifest"]="ReleaseManifestVerified"
    };
    public static bool Handles(string[] args) => args.Length>0 && (args[0].StartsWith("--deployment-",StringComparison.Ordinal)
        || args[0]=="--maintenance-contract" || (args[0] is "--prepare-legacy-review" or "--verify-review-upgrade" && args.Length>1 && args[1]=="--plan"));
    static void Count(MaintenanceResult result,DeploymentPlan plan)
    {
        result.SchemaState=plan.SchemaState; result.ExpectedEvidenceCount=plan.RequiredEvidence.Count;
        result.ExpectedEvidenceDigest=DeploymentDatabase.Digest(plan.RequiredEvidence); result.SnapshotSha256=plan.SnapshotSha256;
        result.SourceLines=plan.SourceLines; result.ChangedSourceLines=plan.ChangedSourceLines; result.MissingSourceLines=plan.MissingSourceLines;
        result.AlreadyIdentifiedSourceLines=plan.AlreadyIdentifiedSourceLines; result.UnknownNotInSource=plan.UnknownNotInSource;
        result.BusinessTablesCompared=plan.Baseline.Tables.Count; result.PreviousTriggersPreserved=plan.Baseline.Triggers.Count;
    }
    public static async Task<int> Run(string[] args)
    {
        if(args[0]=="--deployment-check-result")
        {
            try
            {
                DeploymentDatabase.Require(args.Length==4,"InvalidArguments");
                using var document=JsonDocument.Parse(File.ReadAllText(args[1]));
                DeploymentDatabase.Require(new[]{"ContractVersion","CandidateCommit","Command","Success","FailureCode","Gates"}.All(k=>document.RootElement.TryGetProperty(k,out _)),"IncompleteMachineResult");
                var result=DeploymentDatabase.Load<MaintenanceResult>(args[1]);
                DeploymentDatabase.Require(RequiredGates.TryGetValue(args[2],out var gate) && result.ContractVersion==1 && result.Success
                    && result.Command==args[2] && result.CandidateCommit==args[3] && result.FailureCode.Length==0
                    && result.Gates.TryGetValue(gate,out var passed) && passed,"MachineResultRejected");
                if(args[2] is "--verify-review-upgrade" or "--deployment-apply" or "--deployment-verify")
                    DeploymentDatabase.Require(result.Gates.GetValueOrDefault("BusinessPreserved") && result.Gates.GetValueOrDefault("IntegrityVerified")
                        && result.Gates.GetValueOrDefault("GuardsVerified") && result.Gates.GetValueOrDefault("EvidenceVerified")
                        && result.BusinessTablesCompared>0 && result.ExpectedEvidenceCount>=0
                        && System.Text.RegularExpressions.Regex.IsMatch(result.ExpectedEvidenceDigest,"^[0-9A-F]{64}$")
                        && System.Text.RegularExpressions.Regex.IsMatch(result.SnapshotSha256,"^[0-9A-F]{64}$")
                        && result.ImportedEvidenceCount==result.ExpectedEvidenceCount && result.ImportedEvidenceDigest==result.ExpectedEvidenceDigest,"IncompleteVerificationResult");
                if(args[2]=="--prepare-legacy-review")
                    DeploymentDatabase.Require(result.ExpectedEvidenceCount>=0 && System.Text.RegularExpressions.Regex.IsMatch(result.ExpectedEvidenceDigest,"^[0-9A-F]{64}$")
                        && result.PreparedEvidenceCount==result.ExpectedEvidenceCount && result.PreparedEvidenceDigest==result.ExpectedEvidenceDigest,"IncompleteEvidenceResult");
                if(args[2]=="--deployment-backup")
                    DeploymentDatabase.Require(result.Gates.GetValueOrDefault("IntegrityVerified") && System.Text.RegularExpressions.Regex.IsMatch(result.SnapshotSha256,"^[0-9A-F]{64}$"),"IncompleteBackupResult");
                if(args[2]=="--deployment-plan")
                    DeploymentDatabase.Require(result.SchemaState is "BeforeIdentity" or "IdentityOnly" or "ReviewApplied"
                        && result.BusinessTablesCompared>0 && result.ExpectedEvidenceCount>=0 && System.Text.RegularExpressions.Regex.IsMatch(result.ExpectedEvidenceDigest,"^[0-9A-F]{64}$")
                        && System.Text.RegularExpressions.Regex.IsMatch(result.SnapshotSha256,"^[0-9A-F]{64}$"),"IncompletePlanResult");
                return 0;
            }
            catch { Console.WriteLine("{\"ContractVersion\":1,\"Success\":false,\"FailureCode\":\"MachineResultRejected\"}"); return 3; }
        }
        var signatures=new Dictionary<string,int> { ["--maintenance-contract"]=2,["--deployment-backup"]=4,["--deployment-plan"]=6,["--deployment-freeze"]=4,
            ["--verify-review-upgrade"]=4,["--prepare-legacy-review"]=5,["--deployment-apply"]=4,["--deployment-verify"]=4,["--deployment-restore"]=4,
            ["--deployment-health"]=6,["--deployment-recovery-health"]=6,["--deployment-dp-probe"]=4,["--deployment-compatible"]=4,["--deployment-state"]=6,["--deployment-recovery-inspect"]=4,["--deployment-package"]=3,["--deployment-previous-attempt"]=3,["--deployment-config"]=6,["--deployment-manifest"]=3 };
        var validSignature=signatures.TryGetValue(args[0],out var length) && args.Length==length
            && (args[0] is not ("--verify-review-upgrade" or "--prepare-legacy-review") || args[1]=="--plan");
        var output=validSignature && Path.GetExtension(args[^1])==".json" && !File.Exists(args[^1]) ? args[^1] : "";
        var r=new MaintenanceResult { Command=args[0] };
        try
        {
            DeploymentDatabase.Require(validSignature && output.Length>0 && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(output))),"InvalidArgumentsOrResultPath");
            switch(args[0])
            {
                case "--deployment-dp-probe":
                    ProductionKeyProbe.Verify(args[1],args[2]); break;
                case "--maintenance-contract":
                    DeploymentDatabase.Require(args.Length==2,"InvalidArguments");
                    DeploymentDatabase.Require(typeof(DeploymentMaintenance).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+').Last()==r.CandidateCommit,"ToolApplicationVersionMismatch");
                    break;
                case "--deployment-backup":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); await DeploymentDatabase.Backup(args[1],args[2]); r.SnapshotSha256=await DeploymentDatabase.Hash(args[2]); r.Gates["IntegrityVerified"]=true; break;
                case "--deployment-plan":
                    DeploymentDatabase.Require(args.Length==6,"InvalidArguments");
                    var created=await DeploymentDatabase.Plan(args[1],args[2],args[3]); DeploymentDatabase.Require(!File.Exists(args[4]),"PlanAlreadyExists");
                    DeploymentDatabase.Save(args[4],created); Count(r,created); break;
                case "--deployment-freeze":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); var frozen=DeploymentDatabase.Load<DeploymentPlan>(args[2]);
                    await DeploymentDatabase.SourceUnchanged(frozen); await DeploymentDatabase.Compare(args[1],frozen.Baseline);
                    DeploymentDatabase.Require(frozen.FrozenTargetIdentity.Length==0,"TargetAlreadyFrozen");
                    frozen.FrozenTargetIdentity=DatabaseFileIdentity.Get(args[1]); frozen.FrozenTargetStamp=await DeploymentDatabase.Stamp(args[1]);
                    DeploymentDatabase.Save(args[2],frozen); Count(r,frozen); break;
                case "--verify-review-upgrade":
                    DeploymentDatabase.Require(args.Length==4 && args[1]=="--plan","InvalidArguments"); var rehearsed=DeploymentDatabase.Load<DeploymentPlan>(args[2]);
                    await DeploymentDatabase.Rehearse(rehearsed); DeploymentDatabase.Save(args[2],rehearsed); Count(r,rehearsed); Verified(r); break;
                case "--prepare-legacy-review":
                    DeploymentDatabase.Require(args.Length==5 && args[1]=="--plan","InvalidArguments"); var prepared=DeploymentDatabase.Load<DeploymentPlan>(args[2]);
                    DeploymentDatabase.Require(prepared.RehearsalVerified,"RehearsalRequired"); await DeploymentDatabase.Prepare(args[3],prepared);
                    DeploymentDatabase.Save(args[2],prepared); Count(r,prepared); r.PreparedEvidenceCount=r.ExpectedEvidenceCount; r.PreparedEvidenceDigest=r.ExpectedEvidenceDigest; break;
                case "--deployment-apply":
                case "--deployment-verify":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); var applied=DeploymentDatabase.Load<DeploymentPlan>(args[2]);
                    if(args[0]=="--deployment-apply") await DeploymentDatabase.Apply(args[1],applied); else await DeploymentDatabase.Verify(args[1],applied);
                    Count(r,applied); Verified(r); break;
                case "--deployment-restore":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); var restored=DeploymentDatabase.Load<DeploymentPlan>(args[2]);
                    var restoreStatePath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"state.json");
                    var restoreState=DeploymentDatabase.Load<DeploymentAttempt>(restoreStatePath);
                    DeploymentDatabase.Require(restoreState.ContractVersion==1 && restoreState.Commit==r.CandidateCommit && restoreState.WritersStopped
                        && !restoreState.ApplicationStartAttempted && !restoreState.DatabaseRestoreAttempted
                        && restoreState.Stage is ("TargetFrozen" or "RehearsalVerified" or "EvidencePrepareAttempted" or "EvidenceVerified"
                            or "SchemaChangeAttempted" or "SchemaVerified" or "FilesSwapAttempted" or "FilesSwapped")
                        && restored.ContractVersion==1 && restored.CandidateCommit==r.CandidateCommit
                        && restoreState.TargetIdentity.Length>0 && restoreState.TargetIdentity==restored.FrozenTargetIdentity
                        && restoreState.TargetIdentity==DatabaseFileIdentity.Get(args[1])
                        && restoreState.SnapshotPath==restored.SnapshotPath && restoreState.SnapshotSha256==restored.SnapshotSha256,
                        "RestoreForbiddenAfterStartOrUnknownAttempt");
                    // Persist intent before any restore work. A failed/interrupted restore is never replayed.
                    restoreState.DatabaseRestoreAttempted=true; restoreState.Stage="DatabaseRestoreAttempted";
                    restoreState.Stages.Add(restoreState.Stage); restoreState.UpdatedAtUtc=DateTime.UtcNow;
                    restoreState.BeforeRestorePath=Path.Combine(Path.GetDirectoryName(restoreStatePath)!,"before-restore.db");
                    DeploymentDatabase.Save(restoreStatePath,restoreState);
                    // Keep the current schema/data as well as the original same-attempt snapshot.
                    await DeploymentDatabase.Backup(args[1],restoreState.BeforeRestorePath);
                    restoreState.BeforeRestoreSha256=await DeploymentDatabase.Hash(restoreState.BeforeRestorePath);
                    DeploymentDatabase.Save(restoreStatePath,restoreState);
                    await DeploymentDatabase.Restore(args[1],restored); Count(r,restored); break;
                case "--deployment-health":
                    DeploymentDatabase.Require(args.Length==6 && int.TryParse(args[4],out var processId) && processId>0,"InvalidArguments");
                    var health=JsonSerializer.Deserialize<DeploymentReadiness.Health>(File.ReadAllText(args[2]),new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
                    var expected=await DeploymentReadiness.CheckAsync(args[1],args[3]); DeploymentDatabase.Require(health==expected with { ProcessId=int.Parse(args[4]) },"HealthVersionOrDatabaseMismatch"); break;
                case "--deployment-compatible":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); var manifest=DeploymentDatabase.Load<ReleaseManifest>(args[1]);
                    DeploymentDatabase.Require(manifest.ContractVersion==1 && manifest.ApplicationSha256==await DeploymentDatabase.Hash(Path.Combine(Path.GetDirectoryName(args[1])!,"LaraFashion.dll")),"PreviousReleaseManifestMismatch");
                    await using(var db=await DeploymentDatabase.Read(args[2]))
                        DeploymentDatabase.Require(manifest.Migration== (string)(await DeploymentDatabase.Rows(db,"SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1"))[0][0]!,"PreviousReleaseIncompatible"); break;
                case "--deployment-recovery-health":
                    DeploymentDatabase.Require(int.TryParse(args[4],out var recoveryPid) && recoveryPid>0,"InvalidRecoveryPid");
                    var previousManifest=DeploymentDatabase.Load<ReleaseManifest>(args[3]);
                    DeploymentDatabase.Require(previousManifest.ContractVersion==1 && System.Text.RegularExpressions.Regex.IsMatch(previousManifest.Commit,"^[0-9a-f]{40}$")
                        && previousManifest.ApplicationSha256==await DeploymentDatabase.Hash(Path.Combine(Path.GetDirectoryName(args[3])!,"LaraFashion.dll")),"PreviousReleaseManifestMismatch");
                    await DeploymentDatabase.Classify(args[1]);
                    await using(var recoveredDb=await DeploymentDatabase.Read(args[1]))
                        DeploymentDatabase.Require(previousManifest.Migration==(string)(await DeploymentDatabase.Rows(recoveredDb,"SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1"))[0][0]!,"PreviousReleaseIncompatible");
                    var recoveredHealth=JsonSerializer.Deserialize<DeploymentReadiness.Health>(File.ReadAllText(args[2]),new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
                    var expectedRecoveryHealth=new DeploymentReadiness.Health(1,"healthy",previousManifest.Commit,previousManifest.Migration,DatabaseFileIdentity.Get(args[1]),recoveryPid);
                    DeploymentDatabase.Require(recoveredHealth==expectedRecoveryHealth,"RecoveryHealthVersionOrDatabaseMismatch"); break;
                case "--deployment-manifest":
                    DeploymentDatabase.Require(args.Length==3,"InvalidArguments");
                    var releasePath=Path.Combine(args[1],"release-version.json"); DeploymentDatabase.Require(!File.Exists(releasePath),"ReleaseManifestAlreadyExists");
                    DeploymentDatabase.Save(releasePath,new ReleaseManifest(1,r.CandidateCommit,DeploymentReadiness.ReviewMigration,await DeploymentDatabase.Hash(Path.Combine(args[1],"LaraFashion.dll")))); break;
                case "--deployment-state":
                    DeploymentDatabase.Require(args.Length==6,"InvalidArguments"); RecordState(args[1],args[2],args[3],args[4]); break;
                case "--deployment-recovery-inspect":
                    DeploymentDatabase.Require(args.Length==4,"InvalidArguments"); var previous=DeploymentDatabase.Load<DeploymentAttempt>(args[1]);
                    DeploymentDatabase.Require(previous.ContractVersion==1,"UnsupportedAttemptState"); r.SchemaState=await DeploymentDatabase.Classify(args[2]); break;
                default: throw new DeploymentFailure("UnknownMaintenanceCommand");
                case "--deployment-config":
                    DeploymentDatabase.Require(args.Length==6,"InvalidArguments");
                    var settings=new Dictionary<string,string>();
                    foreach(var raw in File.ReadAllLines(args[1]))
                    {
                        var line=raw.Trim(); if(line.Length==0 || line.StartsWith('#')) continue;
                        var separator=line.IndexOf('='); DeploymentDatabase.Require(separator>0 && !line.EndsWith('\\'),"UnsupportedRuntimeEnvironmentFile");
                        var key=line[..separator]; var value=line[(separator+1)..].Trim();
                        if(value.Length>=2 && ((value[0]=='\"' && value[^1]=='\"') || (value[0]=='\'' && value[^1]=='\''))) value=value[1..^1];
                        DeploymentDatabase.Require(settings.TryAdd(key,value),"DuplicateRuntimeSetting");
                    }
                    DeploymentDatabase.Require(settings.GetValueOrDefault("ASPNETCORE_ENVIRONMENT")=="Production"
                        && new[]{"LARAFASHION_DATABASE_PATH","LARAFASHION_UPLOADS_PATH","LARAFASHION_DATA_PROTECTION_PATH"}.All(k=>Path.IsPathFullyQualified(settings.GetValueOrDefault(k) ?? ""))
                        && DatabaseFileIdentity.Get(settings.GetValueOrDefault("LARAFASHION_DATABASE_PATH") ?? "")==DatabaseFileIdentity.Get(args[2])
                        && Path.GetFullPath(settings.GetValueOrDefault("LARAFASHION_UPLOADS_PATH") ?? "")==Path.GetFullPath(args[3])
                        && Path.GetFullPath(settings.GetValueOrDefault("LARAFASHION_DATA_PROTECTION_PATH") ?? "")==Path.GetFullPath(args[4])
                        && new[]{"JwtSettings__SecretKey","JwtSettings__Issuer","JwtSettings__Audience"}.All(k=>!string.IsNullOrWhiteSpace(settings.GetValueOrDefault(k)))
                        && int.TryParse(settings.GetValueOrDefault("JwtSettings__ExpiryMinutes"),out var expiry) && expiry>0,"RuntimeConfigurationMismatch"); break;
                case "--deployment-previous-attempt":
                    DeploymentDatabase.Require(args.Length==3,"InvalidArguments"); var prior=DeploymentDatabase.Load<DeploymentAttempt>(args[1]);
                    DeploymentDatabase.Require(prior.ContractVersion==1 && prior.Stage is "Completed" or "Recovered" or "FailedBeforeStop","UnresolvedPreviousAttempt"); break;
                case "--deployment-package":
                    DeploymentDatabase.Require(args.Length==3 && Directory.Exists(args[1]),"InvalidArguments");
                    var files=Directory.EnumerateFiles(args[1],"*",SearchOption.AllDirectories).ToArray();
                    DeploymentDatabase.Require(files.All(f=>!new[]{".db",".db-wal",".db-shm",".db-journal",".pem",".key"}.Any(e=>f.EndsWith(e,StringComparison.OrdinalIgnoreCase))
                        && !f.Replace('\\','/').Contains("/uploads/",StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(f).StartsWith("appsettings",StringComparison.OrdinalIgnoreCase)),"SensitivePublishContent"); break;
            }
            r.Gates[RequiredGates[args[0]]]=true; r.Success=true;
        }
        catch(Exception ex) { r.Success=false; r.FailureCode=ex is DeploymentFailure failure ? failure.Code : "MaintenanceOperationFailed"; }
        try { if(output.Length>0) DeploymentDatabase.Save(output,r); } catch { r.Success=false; r.FailureCode="MachineResultWriteFailed"; }
        // Summaries only. Plans contain local protected row fingerprints and never go to Actions.
        Console.WriteLine(JsonSerializer.Serialize(r)); return r.Success ? 0 : 2;
    }
    static void Verified(MaintenanceResult r)
    {
        foreach(var gate in new[] { "BusinessPreserved","IntegrityVerified","GuardsVerified","EvidenceVerified","SchemaVerified" }) r.Gates[gate]=true;
        r.ImportedEvidenceCount=r.ExpectedEvidenceCount; r.ImportedEvidenceDigest=r.ExpectedEvidenceDigest;
    }
    public static void RecordState(string path,string stage,string commit,string previousRelease)
    {
        var stages=new[] { "Prepared","WritersStopAttempted","WritersStopped","BackupVerified","PlanVerified","TargetFrozen","RehearsalVerified","EvidencePrepareAttempted","EvidenceVerified","SchemaChangeAttempted","SchemaVerified","FilesSwapAttempted","FilesSwapped","ApplicationStartAttempted","HealthVerified","Completed" };
        var state=File.Exists(path) ? DeploymentDatabase.Load<DeploymentAttempt>(path) : new DeploymentAttempt { Commit=commit,PreviousRelease=previousRelease };
        DeploymentDatabase.Require(state.ContractVersion==1 && state.Commit==commit,"AttemptVersionMismatch");
        var terminal=stage is "FailedBeforeStop" or "Recovered" or "ManualRecoveryRequired" or "RecoveryStartAttempted";
        DeploymentDatabase.Require(terminal || Array.IndexOf(stages,stage)==state.Stages.Count,"InvalidStageSequence");
        state.Stage=stage; state.Stages.Add(stage); state.UpdatedAtUtc=DateTime.UtcNow;
        state.StopAttempted|=stage=="WritersStopAttempted"; state.WritersStopped|=stage=="WritersStopped";
        state.DatabaseChangeAttempted|=stage is "EvidencePrepareAttempted" or "SchemaChangeAttempted";
        var planPath=Path.Combine(Path.GetDirectoryName(path)!,"plan.json");
        if(File.Exists(planPath))
        {
            var plan=DeploymentDatabase.Load<DeploymentPlan>(planPath);
            state.SnapshotPath=plan.SnapshotPath; state.SnapshotSha256=plan.SnapshotSha256;
            state.EvidenceSourcePath=plan.EvidenceSourcePath; state.EvidenceSourceSha256=plan.EvidenceSourceSha256; state.TargetIdentity=plan.FrozenTargetIdentity;
        }
        state.FilesSwapAttempted|=stage=="FilesSwapAttempted"; state.ApplicationStartAttempted|=stage is "ApplicationStartAttempted" or "RecoveryStartAttempted";
        DeploymentDatabase.Save(path,state);
    }
}
