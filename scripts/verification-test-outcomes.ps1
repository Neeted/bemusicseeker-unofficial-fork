# 通常検証とリリース必須rosterが共有する結果読取り・合否判定。
# dot-sourceだけでは検証やプロセス起動を行わない。

function Get-VerificationOutcomeProperty {
    param(
        [Parameter(Mandatory)]
        [object]$Object,

        [Parameter(Mandatory)]
        [string[]]$Names
    )

    foreach ($name in $Names) {
        if ($Object -is [System.Collections.IDictionary]) {
            if ($Object.Contains($name)) {
                return $Object[$name]
            }
        }
        else {
            $property = $Object.PSObject.Properties[$name]
            if ($null -ne $property) {
                return $property.Value
            }
        }
    }
    return $null
}

function Get-VerificationOutcomeString {
    param(
        [Parameter(Mandatory)]
        [object]$Object,

        [Parameter(Mandatory)]
        [string[]]$Names
    )

    $value = Get-VerificationOutcomeProperty -Object $Object -Names $Names
    if ($null -eq $value) {
        return [string]::Empty
    }
    return [string]$value
}

function Convert-VerificationTrxResults {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $document = [xml](Get-Content -LiteralPath $Path -Raw)
    }
    catch {
        throw "Verification result receipt is not valid TRX/XML: $Path"
    }

    $run = $document.DocumentElement
    if ($null -eq $run -or $run.LocalName -cne 'TestRun' -or
        $null -eq $run.SelectSingleNode("./*[local-name()='Results']")) {
        throw "Verification result receipt is missing TestRun/Results: $Path"
    }
    $summary = $run.SelectSingleNode("./*[local-name()='ResultSummary']")
    if ($null -eq $summary -or [string]::IsNullOrWhiteSpace($summary.GetAttribute('outcome'))) {
        throw "Verification result receipt is missing its run-level outcome: $Path"
    }
    $runOutcome = [string]$summary.GetAttribute('outcome')
    if ($runOutcome -cne 'Completed' -and $runOutcome -cne 'Passed') {
        throw "Verification run-level outcome is not successful: $runOutcome ($Path)"
    }

    $definitions = @{}
    foreach ($definition in @($document.SelectNodes("//*[local-name()='UnitTest']"))) {
        $id = [string]$definition.GetAttribute('id')
        if ([string]::IsNullOrWhiteSpace($id) -or $definitions.ContainsKey($id)) {
            throw "Verification TRX test definition has a missing or duplicate id: $Path"
        }
        $method = $definition.SelectSingleNode("./*[local-name()='TestMethod']")
        if ($null -eq $method -or
            [string]::IsNullOrWhiteSpace($method.GetAttribute('className')) -or
            [string]::IsNullOrWhiteSpace($method.GetAttribute('name'))) {
            throw "Verification TRX test definition is missing its class or method: $Path"
        }
        $name = "$($method.GetAttribute('className')).$($method.GetAttribute('name'))"
        $definitions[$id] = $name
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($result in @($document.SelectNodes("//*[local-name()='UnitTestResult']"))) {
        $testId = [string]$result.GetAttribute('testId')
        if (-not $definitions.ContainsKey($testId)) {
            throw "Verification TRX result is missing its method definition: $Path"
        }
        $name = [string]$definitions[$testId]
        $outcome = [string]$result.GetAttribute('outcome')
        $reason = [string]$result.GetAttribute('reason')
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $message = $result.SelectSingleNode(".//*[local-name()='ErrorInfo']/*[local-name()='Message']")
            if ($null -ne $message) {
                $reason = [string]$message.InnerText
            }
        }
        [void]$results.Add([pscustomobject][ordered]@{
                FullyQualifiedName = $name
                Outcome = $outcome
                Reason = $reason
                SourcePath = $Path
                TestId = $testId
                ExecutionId = [string]$result.GetAttribute('executionId')
            })
    }
    return @($results)
}

function Convert-VerificationJsonResults {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Verification result receipt is not valid JSON: $Path"
    }

    $items = if ($document -is [System.Array]) {
        @($document)
    }
    else {
        $candidate = Get-VerificationOutcomeProperty -Object $document -Names @('results', 'testResults', 'outcomes')
        if ($null -eq $candidate) { @($document) } else { @($candidate) }
    }
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $items) {
        $name = Get-VerificationOutcomeString -Object $item -Names @('fullyQualifiedName', 'fqn', 'testName', 'name')
        $outcome = Get-VerificationOutcomeString -Object $item -Names @('outcome', 'status', 'result')
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($outcome)) {
            throw "Verification JSON result must contain a fullyQualifiedName and outcome: $Path"
        }
        [void]$results.Add([pscustomobject][ordered]@{
                FullyQualifiedName = $name
                Outcome = $outcome
                Reason = Get-VerificationOutcomeString -Object $item -Names @('reason', 'skipReason', 'diagnostic')
                SourcePath = $Path
                TestId = Get-VerificationOutcomeString -Object $item -Names @('testId', 'id')
                ExecutionId = Get-VerificationOutcomeString -Object $item -Names @('executionId', 'execution')
            })
    }
    return @($results)
}

function Read-VerificationTestOutcomes {
    param(
        [Parameter(Mandatory)]
        [Alias('ResultPath', 'TrxPath', 'TrxPaths')]
        [string[]]$ResultPaths
    )

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($path in @($ResultPaths)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            throw 'Verification result path cannot be empty.'
        }
        $resolvedPath = [IO.Path]::GetFullPath($path)
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            throw "Verification result receipt is missing: $resolvedPath"
        }
        $extension = [IO.Path]::GetExtension($resolvedPath)
        $items = if ($extension -ieq '.trx' -or $extension -ieq '.xml') {
            Convert-VerificationTrxResults -Path $resolvedPath
        }
        elseif ($extension -ieq '.json') {
            Convert-VerificationJsonResults -Path $resolvedPath
        }
        else {
            throw "Unsupported verification result receipt format: $resolvedPath"
        }
        if (@($items).Count -eq 0) {
            throw "Verification result receipt is missing test results: $resolvedPath"
        }
        foreach ($item in @($items)) {
            [void]$results.Add($item)
        }
    }
    return @($results)
}

function Read-VerificationOutcomeRoster {
    param(
        [Parameter(Mandatory)]
        [string]$RosterPath
    )

    $resolvedPath = [IO.Path]::GetFullPath($RosterPath)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Verification outcome roster is missing: $resolvedPath"
    }
    try {
        $document = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Verification outcome roster is not valid JSON: $resolvedPath"
    }
    $outcome = Get-VerificationOutcomeProperty -Object $document -Names @('releaseOutcome', 'outcomeGate')
    if ($null -eq $outcome) {
        $outcome = $document
    }
    $required = Get-VerificationOutcomeProperty -Object $outcome -Names @('required', 'requiredTests', 'roster')
    return [pscustomobject][ordered]@{
        Path = $resolvedPath
        Required = @($required)
    }
}

function Convert-VerificationRosterEntries {
    param(
        [object]$RequiredRoster,

        [string[]]$RequiredFqns
    )

    $entries = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $RequiredRoster) {
        foreach ($item in @($RequiredRoster)) {
            $fqn = Get-VerificationOutcomeString -Object $item -Names @('fullyQualifiedName', 'fqn', 'name')
            $contractId = Get-VerificationOutcomeString -Object $item -Names @('contractId', 'id')
            if ([string]::IsNullOrWhiteSpace($fqn)) {
                throw 'Required verification roster entry must contain an exact fully-qualified name.'
            }
            [void]$entries.Add([pscustomobject][ordered]@{
                    ContractId = $contractId
                    FullyQualifiedName = $fqn
                })
        }
    }
    if ($null -ne $RequiredFqns) {
        foreach ($fqn in @($RequiredFqns)) {
            if ([string]::IsNullOrWhiteSpace($fqn)) {
                throw 'Required verification fully-qualified name cannot be empty.'
            }
            [void]$entries.Add([pscustomobject][ordered]@{
                    ContractId = [string]::Empty
                    FullyQualifiedName = $fqn
                })
        }
    }
    if ($entries.Count -eq 0) {
        throw 'Required verification roster cannot be empty.'
    }
    $seenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenContractIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        if (-not $seenNames.Add($entry.FullyQualifiedName)) {
            throw "Required verification roster contains a duplicate FQN: $($entry.FullyQualifiedName)"
        }
        if (-not [string]::IsNullOrWhiteSpace($entry.ContractId) -and -not $seenContractIds.Add($entry.ContractId)) {
            throw "Required verification roster contains a duplicate Contract ID: $($entry.ContractId)"
        }
    }
    return @($entries)
}

function Assert-VerificationTestOutcomes {
    param(
        [Parameter(Mandatory)]
        [Alias('ResultPath', 'TrxPath', 'TrxPaths')]
        [string[]]$ResultPaths,

        [object]$Roster,
        [object]$RequiredRoster,
        [string[]]$RequiredFqns,
        [string]$RosterPath,

        # 既知計画の選択methodは一件以上、release必須rosterだけは正確に一件を要求する。
        [string[]]$SelectedFqns,
        # 任意filterのQuickでは独自にfilterを解釈せず、既知method集合のみを照合する。
        [string[]]$KnownFqns,
        [string]$ReceiptPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RosterPath)) {
        $Roster = Read-VerificationOutcomeRoster -RosterPath $RosterPath
    }
    if ($null -ne $Roster -and $null -eq $RequiredRoster -and $null -eq $RequiredFqns) {
        $RequiredRoster = Get-VerificationOutcomeProperty -Object $Roster -Names @('Required', 'required', 'requiredTests', 'roster')
    }
    $required = if ($null -ne $RequiredRoster -or $null -ne $RequiredFqns) {
        @(Convert-VerificationRosterEntries -RequiredRoster $RequiredRoster -RequiredFqns $RequiredFqns)
    } else { @() }
    if ($PSBoundParameters.ContainsKey('SelectedFqns') -and @($SelectedFqns).Count -eq 0) {
        throw 'Verification selected method set is empty.'
    }
    if ($null -eq $KnownFqns -and $null -ne $SelectedFqns) { $KnownFqns = $SelectedFqns }
    $known = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @($KnownFqns)) { if ($null -ne $name) { [void]$known.Add($name) } }
    $results = @(Read-VerificationTestOutcomes -ResultPaths $ResultPaths)
    if ($results.Count -eq 0) { throw 'Verification result set is missing test results.' }
    $resultsByName = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        if ([string]::IsNullOrWhiteSpace($result.FullyQualifiedName)) {
            throw 'Verification result contains an empty fully-qualified name.'
        }
        if ($null -ne $KnownFqns -and -not $known.Contains($result.FullyQualifiedName)) {
            throw "Unknown verification method result: $($result.FullyQualifiedName)"
        }
        if ($result.Outcome -cne 'Passed') {
            throw "Verification method is not Passed: $($result.FullyQualifiedName) outcome=$($result.Outcome)"
        }
        if (-not $resultsByName.ContainsKey($result.FullyQualifiedName)) {
            $resultsByName[$result.FullyQualifiedName] = [System.Collections.Generic.List[object]]::new()
        }
        [void]$resultsByName[$result.FullyQualifiedName].Add($result)
    }
    foreach ($name in @($SelectedFqns)) {
        if ($null -ne $name -and -not $resultsByName.ContainsKey($name)) {
            throw "Selected verification method is missing: $name"
        }
    }
    $requiredReceipt = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $required) {
        if (-not $resultsByName.ContainsKey($entry.FullyQualifiedName)) {
            throw "Required verification FQN is missing: $($entry.FullyQualifiedName)"
        }
        $matches = @($resultsByName[$entry.FullyQualifiedName])
        if ($matches.Count -ne 1) {
            throw "Required verification FQN has duplicate results: $($entry.FullyQualifiedName) count=$($matches.Count)"
        }
        [void]$requiredReceipt.Add([ordered]@{
                contractId = $entry.ContractId
                fullyQualifiedName = $entry.FullyQualifiedName
                outcome = [string]$matches[0].Outcome
                sourcePath = [string]$matches[0].SourcePath
            })
    }
    $receipt = [ordered]@{
        schemaVersion = 1
        manifestType = 'BeMusicSeeker.VerificationTestOutcomes'
        status = 'passed'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        sourcePaths = @($ResultPaths | ForEach-Object { [IO.Path]::GetFullPath($_) })
        resultCount = $results.Count
        required = @($requiredReceipt)
    }
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $resolvedReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedReceiptPath) -Force)
        [IO.File]::WriteAllText($resolvedReceiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    }
    return [pscustomobject][ordered]@{
        Receipt = $receipt
        Results = @($results)
        Required = @($requiredReceipt)
        Passed = $true
    }
}

function Assert-VerificationOutcomeRoster {
    param(
        [Parameter(Mandatory)]
        [string[]]$ResultPaths,

        [Parameter(Mandatory)]
        [string]$RosterPath,

        [string]$ReceiptPath
    )
    return Assert-VerificationTestOutcomes `
        -ResultPaths $ResultPaths `
        -RosterPath $RosterPath `
        -ReceiptPath $ReceiptPath
}
