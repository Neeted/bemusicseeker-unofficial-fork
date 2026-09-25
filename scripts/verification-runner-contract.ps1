function Get-VerificationRunnerContract {
    # Keep only values consumed by an executable runner.  The lifecycle owns deadline
    # behavior; this file supplies phase budgets and diagnostics folders.
    $phaseDescriptors = @(
        [ordered]@{ Name = 'tool-smoke'; BudgetSeconds = 60; DiagnosticsSegment = 'tool-smoke' }
        [ordered]@{ Name = 'v216-cache-preparation'; BudgetSeconds = 180; DiagnosticsSegment = 'v216-cache-preparation' }
        [ordered]@{ Name = 'current-distribution-publish'; BudgetSeconds = 180; DiagnosticsSegment = 'current-distribution-publish' }
        [ordered]@{ Name = 'existing-data'; BudgetSeconds = 180; DiagnosticsSegment = 'existing-data' }
        [ordered]@{ Name = 'update'; BudgetSeconds = 240; DiagnosticsSegment = 'update' }
        [ordered]@{ Name = 'ProcessIntegration'; BudgetSeconds = 180; DiagnosticsSegment = 'process-integration' }
        [ordered]@{ Name = 'ReleaseAcceptance'; BudgetSeconds = 180; DiagnosticsSegment = 'release-acceptance' }
        [ordered]@{ Name = 'format'; BudgetSeconds = 120; DiagnosticsSegment = 'format' })

    return [pscustomobject][ordered]@{
        PhaseDescriptors = $phaseDescriptors
        RepositoryFormat = [pscustomobject][ordered]@{
            WorkspaceKind = 'folder'
            ProjectEvaluation = 'none'
            VerifiesAllGenuineWorkspaceFiles = $true
            GeneratedRootExclusions = @('artifacts/verification', 'bin', 'obj', '.tmp')
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Get-VerificationRunnerContract | ConvertTo-Json -Depth 12 -Compress
}
