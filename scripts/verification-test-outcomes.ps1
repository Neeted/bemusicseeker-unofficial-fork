# Parser and gate for the exact-FQN release outcome roster.  This file is
# sourceable and intentionally has no entry-point side effects.

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

    $definitions = @{}
    foreach ($definition in @($document.SelectNodes("//*[local-name()='UnitTest']"))) {
        $id = [string]$definition.GetAttribute('id')
        if ([string]::IsNullOrWhiteSpace($id)) {
            continue
        }
        $method = $definition.SelectSingleNode("./*[local-name()='TestMethod']")
        $className = if ($null -eq $method) { [string]::Empty } else { [string]$method.GetAttribute('className') }
        $methodName = if ($null -eq $method) { [string]$definition.GetAttribute('name') } else { [string]$method.GetAttribute('name') }
        $name = if (-not [string]::IsNullOrWhiteSpace($className) -and -not [string]::IsNullOrWhiteSpace($methodName)) {
            "$className.$methodName"
        }
        else {
            [string]$definition.GetAttribute('name')
        }
        $definitions[$id] = $name
    }

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($result in @($document.SelectNodes("//*[local-name()='UnitTestResult']"))) {
        $testId = [string]$result.GetAttribute('testId')
        $name = if ($definitions.ContainsKey($testId)) {
            [string]$definitions[$testId]
        }
        else {
            [string]$result.GetAttribute('fullyQualifiedName')
        }
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = [string]$result.GetAttribute('testName')
        }
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
    $optional = Get-VerificationOutcomeProperty -Object $outcome -Names @('optionalSkipAllowlist', 'optionalSkips')
    return [pscustomobject][ordered]@{
        Path = $resolvedPath
        Required = @($required)
        OptionalSkipAllowlist = @($optional)
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

function Convert-VerificationOptionalEntries {
    param(
        [object]$OptionalSkipAllowlist
    )

    $entries = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $OptionalSkipAllowlist) {
        foreach ($item in @($OptionalSkipAllowlist)) {
            if ($item -is [string]) {
                $fqn = [string]$item
                $reason = [string]::Empty
            }
            else {
                $fqn = Get-VerificationOutcomeString -Object $item -Names @('fullyQualifiedName', 'fqn', 'name')
                $reason = Get-VerificationOutcomeString -Object $item -Names @('reason', 'skipReason')
            }
            if ([string]::IsNullOrWhiteSpace($fqn)) {
                throw 'Optional skip allowlist entry must contain an exact fully-qualified name.'
            }
            if ([string]::IsNullOrWhiteSpace($reason)) {
                throw "Optional skip allowlist reason is required for: $fqn"
            }
            [void]$entries.Add([pscustomobject][ordered]@{
                    FullyQualifiedName = $fqn
                    Reason = $reason.Trim()
                })
        }
    }
    $seenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        if (-not $seenNames.Add($entry.FullyQualifiedName)) {
            throw "Optional skip allowlist contains a duplicate FQN: $($entry.FullyQualifiedName)"
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

        [object]$OptionalSkipAllowlist,

        [string]$ReceiptPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RosterPath)) {
        $loadedRoster = Read-VerificationOutcomeRoster -RosterPath $RosterPath
        $Roster = $loadedRoster
    }
    $rosterObject = $Roster
    $requiredObject = $RequiredRoster
    $optionalObject = $OptionalSkipAllowlist
    if ($null -ne $rosterObject) {
        $requiredFromRoster = Get-VerificationOutcomeProperty -Object $rosterObject -Names @('Required', 'required', 'requiredTests', 'roster')
        if ($null -eq $requiredObject -and $null -eq $RequiredFqns) {
            $requiredObject = $requiredFromRoster
        }
        $optionalFromRoster = Get-VerificationOutcomeProperty -Object $rosterObject -Names @('OptionalSkipAllowlist', 'optionalSkipAllowlist', 'optionalSkips')
        if ($null -eq $optionalObject) {
            $optionalObject = $optionalFromRoster
        }
    }
    $required = Convert-VerificationRosterEntries -RequiredRoster $requiredObject -RequiredFqns $RequiredFqns
    $optional = Convert-VerificationOptionalEntries -OptionalSkipAllowlist $optionalObject
    $optionalByName = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $optional) {
        $optionalByName[$entry.FullyQualifiedName] = $entry
    }
    $requiredByName = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $required) {
        $requiredByName[$entry.FullyQualifiedName] = $entry
        if ($optionalByName.ContainsKey($entry.FullyQualifiedName)) {
            throw "A required FQN cannot be an optional skip: $($entry.FullyQualifiedName)"
        }
    }

    $results = @(Read-VerificationTestOutcomes -ResultPaths $ResultPaths)
    $resultsByName = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([StringComparer]::Ordinal)
    foreach ($result in $results) {
        if ([string]::IsNullOrWhiteSpace($result.FullyQualifiedName)) {
            throw 'Verification result contains an empty fully-qualified name.'
        }
        if (-not $resultsByName.ContainsKey($result.FullyQualifiedName)) {
            $resultsByName[$result.FullyQualifiedName] = [System.Collections.Generic.List[object]]::new()
        }
        [void]$resultsByName[$result.FullyQualifiedName].Add($result)
    }

    $requiredReceipt = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $required) {
        $matches = if ($resultsByName.ContainsKey($entry.FullyQualifiedName)) {
            @($resultsByName[$entry.FullyQualifiedName])
        }
        else {
            @()
        }
        if ($matches.Count -eq 0) {
            throw "Required verification FQN is missing: $($entry.FullyQualifiedName)"
        }
        if ($matches.Count -ne 1) {
            throw "Required verification FQN has duplicate results: $($entry.FullyQualifiedName) count=$($matches.Count)"
        }
        $result = $matches[0]
        if ($result.Outcome -cne 'Passed') {
            throw "Required verification FQN is not Passed: $($entry.FullyQualifiedName) outcome=$($result.Outcome)"
        }
        [void]$requiredReceipt.Add([ordered]@{
                contractId = $entry.ContractId
                fullyQualifiedName = $entry.FullyQualifiedName
                outcome = [string]$result.Outcome
                sourcePath = [string]$result.SourcePath
            })
    }

    $optionalReceipt = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $optional) {
        if (-not $resultsByName.ContainsKey($entry.FullyQualifiedName)) {
            continue
        }
        $matches = @($resultsByName[$entry.FullyQualifiedName])
        if ($matches.Count -ne 1) {
            throw "Optional verification FQN has duplicate results: $($entry.FullyQualifiedName) count=$($matches.Count)"
        }
        $result = $matches[0]
        if ($result.Outcome -cne 'Passed') {
            if ($result.Outcome -cne 'Skipped') {
                throw "Optional verification FQN has unsupported non-passed outcome: $($entry.FullyQualifiedName) outcome=$($result.Outcome)"
            }
            [void]$optionalReceipt.Add([ordered]@{
                    fullyQualifiedName = $entry.FullyQualifiedName
                    outcome = [string]$result.Outcome
                    reason = $entry.Reason
                    sourcePath = [string]$result.SourcePath
                })
        }
    }

    foreach ($result in $results) {
        if ($requiredByName.ContainsKey($result.FullyQualifiedName) -or
            $optionalByName.ContainsKey($result.FullyQualifiedName)) {
            continue
        }
        if ($result.Outcome -cne 'Passed') {
            throw "Unknown or non-allowlisted verification result: $($result.FullyQualifiedName) outcome=$($result.Outcome)"
        }
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        manifestType = 'BeMusicSeeker.VerificationTestOutcomes'
        status = 'passed'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        sourcePaths = @($ResultPaths | ForEach-Object { [IO.Path]::GetFullPath($_) })
        resultCount = $results.Count
        required = @($requiredReceipt)
        optionalSkips = @($optionalReceipt)
    }
    if (-not [string]::IsNullOrWhiteSpace($ReceiptPath)) {
        $resolvedReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedReceiptPath) -Force)
        [IO.File]::WriteAllText(
            $resolvedReceiptPath,
            ($receipt | ConvertTo-Json -Depth 12),
            [Text.UTF8Encoding]::new($false))
    }
    return [pscustomobject][ordered]@{
        Receipt = $receipt
        Results = @($results)
        Required = @($requiredReceipt)
        OptionalSkips = @($optionalReceipt)
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
