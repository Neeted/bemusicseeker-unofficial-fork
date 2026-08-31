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
        ExecutionDeadline = 'portable-test-start+FunctionalTimeoutSeconds'
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

    $v216AcceptanceReceiptRelativePath = 'release-acceptance/v216-first-hop/v216-first-hop-acceptance.json'
    $v216AcceptanceReceiptResults = @(
        [ordered]@{
            ContractId = 'UPD-V216-HAPPY'
            FullyQualifiedName = 'BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.HappyPath'
        }
        [ordered]@{
            ContractId = 'UPD-V216-LOCK'
            FullyQualifiedName = 'BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.ManagedFileLockCharacterization'
        })
    $v216AcceptanceReceipt = [ordered]@{
        RelativePath = $v216AcceptanceReceiptRelativePath
        Format = 'json'
        RequiredCardinality = 'exactly-once'
        RequiredOutcome = 'Passed'
        RequiredResults = $v216AcceptanceReceiptResults
    }

    $releaseOutcomeGate = [ordered]@{
        Owner = 'Assert-VerificationTestOutcomes'
        ContractIds = @('REL-CRITICAL-ROSTER', 'REL-OPTIONAL-SKIP')
        RosterPath = 'devdocs/acceptance/v216-first-hop/artifact.json'
        RequiredFqnCardinality = 'exactly-once'
        RequiredOutcome = 'Passed'
        OptionalSkip = 'exact-fqn-allowlist-with-non-empty-reason'
        UnknownNonPassed = 'fail'
        Inputs = @('Functional', 'ProcessIntegration', 'ReleaseAcceptance')
        ReceiptFileName = 'release-outcomes.json'
        ReceiptInputs = @(
            [ordered]@{
                Name = 'V216FirstHopAcceptance'
                RelativePath = $v216AcceptanceReceiptRelativePath
                Format = 'json'
            })
    }

    $v216FirstHop = [ordered]@{
        ContractIds = @('UPD-V216-HAPPY', 'UPD-V216-LOCK', 'REL-V216-ID')
        ArtifactMetadataPath = 'devdocs/acceptance/v216-first-hop/artifact.json'
        AcceptanceScript = 'scripts/accept-v216-first-hop.ps1'
        ArtifactVersion = '2.1.6.0'
        ArtifactSizeBytes = 11260709
        ArtifactSha256 = 'C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2'
        ArtifactSelection = 'checked-in-single-path'
        MissingOrMismatch = 'fail-closed'
        NoFallback = $true
        LegacyUpdaterProtocol = 'protocol-1-legacy-arguments-and-close'
        StartupCompletion = 'bounded-exit-and-full-stream-pid-drain'
        CurrentUpdaterBaseline = 'ab9d97ed3f53dab80fb2894f20f44abdfb6fed32'
        CurrentUpdaterBaselinePurpose = 'separate-current-updater-compatibility-and-recovery-lane'
        AcceptanceReceipt = [pscustomobject]$v216AcceptanceReceipt
    }

    return [pscustomobject][ordered]@{
        Schema = 'BeMusicSeeker.VerifyRefactor.RunnerContract.v1'
        CanonicalFunctional = [pscustomobject]$canonicalFunctional
        V216FirstHop = [pscustomobject]$v216FirstHop
        ReleaseOutcomeGate = [pscustomobject]$releaseOutcomeGate
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
                ReleaseOutcomeGate = [pscustomobject]$releaseOutcomeGate
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
        $Contract.CanonicalFunctional.ExecutionDeadline -cne 'portable-test-start+FunctionalTimeoutSeconds' -or
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

    foreach ($gate in @($Contract.ReleaseOutcomeGate, $Contract.Full.DistributionArtifact.ReleaseOutcomeGate)) {
        if ($null -eq $gate -or
            $gate.Owner -cne 'Assert-VerificationTestOutcomes' -or
            [string]::Join('|', @($gate.ContractIds)) -cne 'REL-CRITICAL-ROSTER|REL-OPTIONAL-SKIP' -or
            $gate.RosterPath -cne 'devdocs/acceptance/v216-first-hop/artifact.json' -or
            $gate.RequiredFqnCardinality -cne 'exactly-once' -or
            $gate.RequiredOutcome -cne 'Passed' -or
            $gate.OptionalSkip -cne 'exact-fqn-allowlist-with-non-empty-reason' -or
            $gate.UnknownNonPassed -cne 'fail' -or
            [string]::Join('|', @($gate.Inputs)) -cne 'Functional|ProcessIntegration|ReleaseAcceptance' -or
            $gate.ReceiptFileName -cne 'release-outcomes.json') {
            throw 'Release outcome gate contract is invalid.'
        }
        $receiptInputs = @($gate.ReceiptInputs)
        if ($receiptInputs.Count -ne 1 -or
            $receiptInputs[0].Name -cne 'V216FirstHopAcceptance' -or
            $receiptInputs[0].RelativePath -cne 'release-acceptance/v216-first-hop/v216-first-hop-acceptance.json' -or
            $receiptInputs[0].Format -cne 'json') {
            throw 'Release outcome gate receipt inputs are invalid.'
        }
    }
    $v216 = $Contract.V216FirstHop
    if ($null -eq $v216 -or
        [string]::Join('|', @($v216.ContractIds)) -cne 'UPD-V216-HAPPY|UPD-V216-LOCK|REL-V216-ID' -or
        $v216.ArtifactMetadataPath -cne 'devdocs/acceptance/v216-first-hop/artifact.json' -or
        $v216.AcceptanceScript -cne 'scripts/accept-v216-first-hop.ps1' -or
        $v216.ArtifactVersion -cne '2.1.6.0' -or
        $v216.ArtifactSizeBytes -ne 11260709 -or
        $v216.ArtifactSha256 -cne 'C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2' -or
        $v216.ArtifactSelection -cne 'checked-in-single-path' -or
        $v216.MissingOrMismatch -cne 'fail-closed' -or
        -not [bool]$v216.NoFallback -or
        $v216.LegacyUpdaterProtocol -cne 'protocol-1-legacy-arguments-and-close' -or
        $v216.StartupCompletion -cne 'bounded-exit-and-full-stream-pid-drain' -or
        $v216.CurrentUpdaterBaseline -cne 'ab9d97ed3f53dab80fb2894f20f44abdfb6fed32' -or
        $v216.CurrentUpdaterBaselinePurpose -cne 'separate-current-updater-compatibility-and-recovery-lane') {
        throw 'v2.1.6.0 first-hop contract is invalid.'
    }
    $acceptanceReceipt = $v216.AcceptanceReceipt
    $requiredResults = @($acceptanceReceipt.RequiredResults)
    if ($null -eq $acceptanceReceipt -or
        $acceptanceReceipt.RelativePath -cne 'release-acceptance/v216-first-hop/v216-first-hop-acceptance.json' -or
        $acceptanceReceipt.Format -cne 'json' -or
        $acceptanceReceipt.RequiredCardinality -cne 'exactly-once' -or
        $acceptanceReceipt.RequiredOutcome -cne 'Passed' -or
        $requiredResults.Count -ne 2 -or
        $requiredResults[0].ContractId -cne 'UPD-V216-HAPPY' -or
        $requiredResults[0].FullyQualifiedName -cne 'BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.HappyPath' -or
        $requiredResults[1].ContractId -cne 'UPD-V216-LOCK' -or
        $requiredResults[1].FullyQualifiedName -cne 'BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.ManagedFileLockCharacterization') {
        throw 'v2.1.6.0 first-hop acceptance receipt contract is invalid.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $contract = Get-VerificationRunnerContract
    Assert-VerificationRunnerContract -Contract $contract
    $contract | ConvertTo-Json -Depth 16 -Compress
}
