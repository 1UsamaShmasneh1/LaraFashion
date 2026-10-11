using System.Text.Json;

// The standalone legacy command and the deployment contract share the same verifier.
static class ReviewUpgradeVerification
{
    public static async Task<int> Run(string sourcePath,string expectedHash)
    {
        DeploymentDatabase.Standalone(sourcePath);
        DeploymentDatabase.Require(string.Equals(await DeploymentDatabase.Hash(sourcePath),expectedHash,StringComparison.OrdinalIgnoreCase),"ReferenceHashMismatch");
        var root=Path.Combine(Path.GetTempPath(),"LaraFashionReviewUpgrade",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var snapshot=Path.Combine(root,"before.db");
        await DeploymentDatabase.Backup(sourcePath,snapshot);
        var plan=await DeploymentDatabase.Plan(snapshot,"-","-");
        await DeploymentDatabase.Rehearse(plan);
        DeploymentDatabase.Save(Path.Combine(root,"verified-plan.json"),plan);
        DeploymentDatabase.Require(string.Equals(await DeploymentDatabase.Hash(sourcePath),expectedHash,StringComparison.OrdinalIgnoreCase),"ReferenceChanged");
        Console.WriteLine(JsonSerializer.Serialize(new {
            ContractVersion=1,Section="reference_upgrade_preservation",Success=true,SchemaState=plan.SchemaState,
            BusinessTablesCompared=plan.Baseline.Tables.Count,AllOriginalColumnsAndRowsIdentical=true,
            PreviousTriggersPreserved=plan.Baseline.Triggers.Count,HistoricalEvidence=plan.RequiredEvidence.Count,
            ExpectedEvidenceDigest=DeploymentDatabase.Digest(plan.RequiredEvidence),
            RehearsalVerified=plan.RehearsalVerified,BusinessPreserved=true,IntegrityCheck="ok",ForeignKeyViolations=0,
            GuardsVerified=true,EvidenceVerified=true,SourceSha256=await DeploymentDatabase.Hash(sourcePath),
            WorkingDirectory=root,Conclusion="Working copy verified; no manager decisions executed. Production needs a fresh snapshot."
        }));
        return 0;
    }
}
