function Write-LegacyConfig {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Settings
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $document = [Xml.XmlDocument]::new()
    $configuration = $document.CreateElement('configuration')
    [void]$document.AppendChild($configuration)
    $userSettings = $document.CreateElement('userSettings')
    [void]$configuration.AppendChild($userSettings)
    $section = $document.CreateElement('BeMusicSeeker.Properties.Settings')
    [void]$userSettings.AppendChild($section)

    foreach ($name in $Settings.Keys) {
        $setting = $document.CreateElement('setting')
        $setting.SetAttribute('name', [string]$name)
        $value = $document.CreateElement('value')
        if ([string]$name -eq 'AssemblyVersion') {
            try {
                $version = [Version]([string]$Settings[$name])
            }
            catch {
                throw "AssemblyVersion must contain four non-negative numeric components: $($Settings[$name])"
            }
            if ($version.Build -lt 0 -or $version.Revision -lt 0) {
                throw "AssemblyVersion must contain four non-negative numeric components: $($Settings[$name])"
            }
            $versionParts = @($version.Major, $version.Minor, $version.Build, $version.Revision)

            $setting.SetAttribute('serializeAs', 'Xml')
            $versionElement = $document.CreateElement('SerializableVersion')
            $componentNames = @('Major', 'Minor', 'Build', 'Revision')
            for ($index = 0; $index -lt $componentNames.Count; $index++) {
                $component = $componentNames[$index]
                $element = $document.CreateElement($component)
                $element.InnerText = [string]$versionParts[$index]
                [void]$versionElement.AppendChild($element)
            }
            [void]$value.AppendChild($versionElement)
        }
        else {
            $setting.SetAttribute('serializeAs', 'String')
            $value.InnerText = [string]$Settings[$name]
        }
        [void]$setting.AppendChild($value)
        [void]$section.AppendChild($setting)
    }
    $document.Save($Path)
}
