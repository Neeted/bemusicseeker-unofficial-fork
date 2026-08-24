function Get-VerificationRunnerContract {
    $canonicalFunctional = [ordered]@{
        Owner = 'Invoke-CanonicalFunctionalVerification'
        InvocationCount = 1
        TimeoutArgument = 'FunctionalTimeoutSeconds'
        DiagnosticsRootArgument = 'DiagnosticsRoot'
        DiagnosticsRootOwnership = 'caller-owned'
        DiagnosticsLayout = 'run-root/{restore,build,functional}'
        Stages = @(
            'locked-restore'
            'release-build'
            'built-output-validation'
            'functional-shards'
            'repository-whitespace')
        FunctionalFilter = 'functional-filter'
        FunctionalTopology = 'existing'
        ExecutionDeadline = 'canonical-start+FunctionalTimeoutSeconds'
        FailureCleanupDeadline = 'execution-deadline+10-seconds'
    }

    $fullPhaseDescriptors = @(
        [ordered]@{
            Name = 'tool-restore'
            BudgetSeconds = 120
            DiagnosticsSegment = 'tool-restore'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'tool-smoke'
            BudgetSeconds = 60
            DiagnosticsSegment = 'tool-smoke'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'current-distribution-publish'
            BudgetSeconds = 180
            DiagnosticsSegment = 'current-distribution-publish'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'baseline-preparation'
            BudgetSeconds = 300
            DiagnosticsSegment = 'baseline-preparation'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'existing-data'
            BudgetSeconds = 180
            DiagnosticsSegment = 'existing-data'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'update'
            BudgetSeconds = 240
            DiagnosticsSegment = 'update'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'ProcessIntegration'
            BudgetSeconds = 180
            DiagnosticsSegment = 'process-integration'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'ReleaseAcceptance'
            BudgetSeconds = 180
            DiagnosticsSegment = 'release-acceptance'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'format'
            BudgetSeconds = 120
            DiagnosticsSegment = 'format'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        }
        [ordered]@{
            Name = 'analyzer'
            BudgetSeconds = 180
            DiagnosticsSegment = 'analyzer'
            ExecutionOwner = 'full-runner'
            Monitored = $true
            FailureContract = 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure'
        })

    return [pscustomobject][ordered]@{
        Schema = 'BeMusicSeeker.VerifyRefactor.RunnerContract.v1'
        CanonicalFunctional = [pscustomobject]$canonicalFunctional
        ModeMappings = @(
            [pscustomobject][ordered]@{
                Name = 'Quick-no-filter'
                Mode = 'Quick'
                FilterState = 'empty'
                CanonicalFunctionalInvocationCount = 1
                CanonicalFunctionalOwner = $canonicalFunctional.Owner
                TimeoutArgument = $canonicalFunctional.TimeoutArgument
                DiagnosticsRootOwnership = $canonicalFunctional.DiagnosticsRootOwnership
                DiagnosticsLayout = $canonicalFunctional.DiagnosticsLayout
                Route = 'canonical-functional'
            }
            [pscustomobject][ordered]@{
                Name = 'Functional'
                Mode = 'Functional'
                FilterState = 'empty'
                CanonicalFunctionalInvocationCount = 1
                CanonicalFunctionalOwner = $canonicalFunctional.Owner
                TimeoutArgument = $canonicalFunctional.TimeoutArgument
                DiagnosticsRootOwnership = $canonicalFunctional.DiagnosticsRootOwnership
                DiagnosticsLayout = $canonicalFunctional.DiagnosticsLayout
                Route = 'canonical-functional'
            }
            [pscustomobject][ordered]@{
                Name = 'Full'
                Mode = 'Full'
                FilterState = 'empty'
                CanonicalFunctionalInvocationCount = 1
                CanonicalFunctionalOwner = $canonicalFunctional.Owner
                TimeoutArgument = $canonicalFunctional.TimeoutArgument
                DiagnosticsRootOwnership = $canonicalFunctional.DiagnosticsRootOwnership
                DiagnosticsLayout = $canonicalFunctional.DiagnosticsLayout
                Route = 'full-with-canonical-functional'
                CanonicalFunctionalBeforePostFunctionalPhases = $true
            }
            [pscustomobject][ordered]@{
                Name = 'Quick-with-filter'
                Mode = 'Quick'
                FilterState = 'provided'
                CanonicalFunctionalInvocationCount = 0
                CanonicalFunctionalOwner = $canonicalFunctional.Owner
                TimeoutArgument = 'FunctionalTimeoutSeconds'
                DiagnosticsRootOwnership = 'caller-owned'
                DiagnosticsLayout = 'run-root/{restore,functional}'
                Route = 'filtered-quick'
            })
        Full = [pscustomobject][ordered]@{
            CanonicalFunctionalInvocationCount = 1
            CanonicalFunctionalOwner = $canonicalFunctional.Owner
            CanonicalFunctionalTimeoutArgument = $canonicalFunctional.TimeoutArgument
            CanonicalFunctionalDiagnosticsRootOwnership = $canonicalFunctional.DiagnosticsRootOwnership
            CanonicalFunctionalDiagnosticsLayout = $canonicalFunctional.DiagnosticsLayout
            IndependentFunctionalRoutes = @()
            OrderedOperations = @(
                'tool-restore'
                'canonical-functional'
                'tool-smoke'
                'current-distribution-publish'
                'baseline-preparation'
                'existing-data'
                'update'
                'ProcessIntegration'
                'ReleaseAcceptance'
                'format'
                'analyzer')
            PhaseDescriptors = $fullPhaseDescriptors
            PostFunctionalStartsWith = 'current-distribution-publish'
            RepositoryFormat = [pscustomobject][ordered]@{
                WorkspaceKind = 'folder'
                ProjectEvaluation = 'none'
                VerifiesAllGenuineWorkspaceFiles = $true
                GeneratedRootExclusions = @(
                    'artifacts/verification'
                    'bin'
                    'obj')
            }
            BaselinePreparation = [pscustomobject][ordered]@{
                ExpandedSourceRoot = 'external-temp-root'
                RunRootContents = 'package-and-manifest-only'
                Cleanup = 'finally'
            }
            DistributionArtifact = [pscustomobject][ordered]@{
                ManifestSchemaVersion = 1
                ManifestMode = 'mandatory'
                ArtifactRoot = 'run-root/distribution'
                Selection = 'exact-version'
                NoLatestScan = $true
                NoRepublishByConsumers = $true
                Consumers = @('existing-data', 'update', 'ProcessIntegration', 'ReleaseAcceptance')
                CreationIdentity = [pscustomobject][ordered]@{
                    Capture = 'baseline-preparation'
                    ExpectedFields = @('runId', 'artifactId', 'manifestSha256', 'manifestSeal')
                    ExactMatch = $true
                    Revalidation = 'before-and-after-every-consumer-and-final'
                }
            }
        }
    }
}

function Assert-VerificationRunnerContract {
    param(
        [Parameter(Mandatory)]
        [object]$Contract
    )

    if ($Contract.Schema -cne 'BeMusicSeeker.VerifyRefactor.RunnerContract.v1') {
        throw 'Unexpected verification runner contract schema.'
    }
    if ($Contract.CanonicalFunctional.Owner -cne 'Invoke-CanonicalFunctionalVerification' -or
        $Contract.CanonicalFunctional.InvocationCount -ne 1 -or
        $Contract.CanonicalFunctional.TimeoutArgument -cne 'FunctionalTimeoutSeconds' -or
        $Contract.CanonicalFunctional.DiagnosticsRootArgument -cne 'DiagnosticsRoot' -or
        $Contract.CanonicalFunctional.DiagnosticsRootOwnership -cne 'caller-owned' -or
        $Contract.CanonicalFunctional.DiagnosticsLayout -cne 'run-root/{restore,build,functional}' -or
        $Contract.CanonicalFunctional.ExecutionDeadline -cne 'canonical-start+FunctionalTimeoutSeconds' -or
        $Contract.CanonicalFunctional.FailureCleanupDeadline -cne 'execution-deadline+10-seconds') {
        throw 'Canonical Functional runner contract is invalid.'
    }

    $expectedBudgets = [ordered]@{
        'tool-restore' = 120
        'tool-smoke' = 60
        'current-distribution-publish' = 180
        'baseline-preparation' = 300
        'existing-data' = 180
        'update' = 240
        'ProcessIntegration' = 180
        'ReleaseAcceptance' = 180
        'format' = 120
        'analyzer' = 180
    }
    $phases = @($Contract.Full.PhaseDescriptors)
    if ($phases.Count -ne $expectedBudgets.Count) {
        throw 'Full runner contract phase count is invalid.'
    }
    foreach ($phase in $phases) {
        if (-not $expectedBudgets.Contains($phase.Name) -or
            $phase.BudgetSeconds -ne $expectedBudgets[$phase.Name] -or
            [string]::IsNullOrWhiteSpace($phase.DiagnosticsSegment) -or
            -not [bool]$phase.Monitored -or
            $phase.FailureContract -cne 'primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure') {
            throw "Full runner contract phase is invalid: $($phase.Name)."
        }
    }
    if (@($Contract.Full.IndependentFunctionalRoutes).Count -ne 0 -or
        $Contract.Full.CanonicalFunctionalInvocationCount -ne 1 -or
        $Contract.Full.CanonicalFunctionalOwner -cne $Contract.CanonicalFunctional.Owner -or
        $Contract.Full.CanonicalFunctionalTimeoutArgument -cne $Contract.CanonicalFunctional.TimeoutArgument -or
        $Contract.Full.CanonicalFunctionalDiagnosticsRootOwnership -cne $Contract.CanonicalFunctional.DiagnosticsRootOwnership -or
        $Contract.Full.CanonicalFunctionalDiagnosticsLayout -cne $Contract.CanonicalFunctional.DiagnosticsLayout -or
        $Contract.Full.RepositoryFormat.WorkspaceKind -cne 'folder' -or
        $Contract.Full.RepositoryFormat.ProjectEvaluation -cne 'none' -or
        -not [bool]$Contract.Full.RepositoryFormat.VerifiesAllGenuineWorkspaceFiles -or
        [string]::Join('|', @($Contract.Full.RepositoryFormat.GeneratedRootExclusions)) -cne 'artifacts/verification|bin|obj' -or
        $Contract.Full.BaselinePreparation.ExpandedSourceRoot -cne 'external-temp-root' -or
        $Contract.Full.BaselinePreparation.RunRootContents -cne 'package-and-manifest-only' -or
        $Contract.Full.BaselinePreparation.Cleanup -cne 'finally' -or
        $Contract.Full.DistributionArtifact.ManifestSchemaVersion -ne 1 -or
        $Contract.Full.DistributionArtifact.ManifestMode -cne 'mandatory' -or
        $Contract.Full.DistributionArtifact.ArtifactRoot -cne 'run-root/distribution' -or
        $Contract.Full.DistributionArtifact.Selection -cne 'exact-version' -or
        -not [bool]$Contract.Full.DistributionArtifact.NoLatestScan -or
        -not [bool]$Contract.Full.DistributionArtifact.NoRepublishByConsumers -or
        $Contract.Full.DistributionArtifact.CreationIdentity.Capture -cne 'baseline-preparation' -or
        [string]::Join('|', @($Contract.Full.DistributionArtifact.CreationIdentity.ExpectedFields)) -cne 'runId|artifactId|manifestSha256|manifestSeal' -or
        -not [bool]$Contract.Full.DistributionArtifact.CreationIdentity.ExactMatch -or
        $Contract.Full.DistributionArtifact.CreationIdentity.Revalidation -cne 'before-and-after-every-consumer-and-final') {
        throw 'Full runner contract must delegate Functional work exactly once to the canonical owner.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $contract = Get-VerificationRunnerContract
    Assert-VerificationRunnerContract -Contract $contract
    $contract | ConvertTo-Json -Depth 16 -Compress
}
