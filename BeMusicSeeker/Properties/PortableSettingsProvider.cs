using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Properties;

public sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
{
	private const string SettingsSectionName = "BeMusicSeeker.Properties.Settings";

	public override void Initialize(string name, NameValueCollection config)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			name = nameof(PortableSettingsProvider);
		}
		config ??= new NameValueCollection();
		base.Initialize(name, config);
	}

	public override string ApplicationName
	{
		get
		{
			return AppDomain.CurrentDomain.FriendlyName;
		}
		set
		{
		}
	}

	public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection collection)
	{
		SettingsPropertyValueCollection settingsPropertyValueCollection = new SettingsPropertyValueCollection();
		Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> dictionary = LoadSettingMap();
		foreach (SettingsProperty item in collection)
		{
			SettingsPropertyValue settingsPropertyValue = new SettingsPropertyValue(item);
			if (dictionary.TryGetValue(item.Name, out var value))
			{
				settingsPropertyValue.SerializedValue = value.serializedValue;
			}
			else
			{
				settingsPropertyValue.SerializedValue = item.DefaultValue;
			}
			settingsPropertyValue.IsDirty = false;
			settingsPropertyValueCollection.Add(settingsPropertyValue);
		}
		return settingsPropertyValueCollection;
	}

	public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection collection)
	{
		try
		{
			Directory.CreateDirectory(PortableSettingsPath.ConfigDirectoryPath);
			if (File.Exists(PortableSettingsPath.UserConfigPath))
			{
				FileInfo fileInfo = new FileInfo(PortableSettingsPath.UserConfigPath);
				if (fileInfo.IsReadOnly)
				{
					UnauthorizedAccessException ex = new UnauthorizedAccessException("Portable settings file is read-only.");
					NLogWrapper.TraceLogger?.Error(ex, "portable_settings_save blocked_readonly path=" + PortableSettingsPath.UserConfigPath);
					WriteFallbackErrorLog("portable_settings_save blocked_readonly path=" + PortableSettingsPath.UserConfigPath, ex);
					return;
				}
			}
			XDocument xDocument = LoadDocumentForUpdate();
			XElement xElement = xDocument.Root?.Element("userSettings")?.Element(SettingsSectionName);
			if (xElement == null)
			{
				return;
			}
			foreach (SettingsPropertyValue item in collection)
			{
				string serialized = GetSerializedValue(item);
				string value = item.Property.SerializeAs.ToString();
				XElement xElement2 = xElement.Elements("setting").FirstOrDefault((XElement e) => string.Equals((string)e.Attribute("name"), item.Name, StringComparison.Ordinal));
				if (xElement2 == null)
				{
					xElement2 = new XElement("setting");
					xElement2.SetAttributeValue("name", item.Name);
					xElement.Add(xElement2);
				}
				xElement2.SetAttributeValue("serializeAs", value);
				XElement xElement3 = xElement2.Element("value");
				if (xElement3 == null)
				{
					xElement3 = new XElement("value");
					xElement2.Add(xElement3);
				}
				xElement3.RemoveNodes();
				if (item.Property.SerializeAs == SettingsSerializeAs.Xml)
				{
					if (!TrySetXmlValue(xElement3, serialized))
					{
						xElement3.Value = serialized;
					}
				}
				else
				{
					xElement3.Value = serialized;
				}
			}
			xDocument.Save(PortableSettingsPath.UserConfigPath);
		}
		catch (Exception ex)
		{
			NLogWrapper.TraceLogger?.Error(ex, "portable_settings_save failed path=" + PortableSettingsPath.UserConfigPath);
			WriteFallbackErrorLog("portable_settings_save failed path=" + PortableSettingsPath.UserConfigPath, ex);
		}
	}

	public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property)
	{
		return new SettingsPropertyValue(property);
	}

	public void Reset(SettingsContext context)
	{
		try
		{
			if (File.Exists(PortableSettingsPath.UserConfigPath))
			{
				File.Delete(PortableSettingsPath.UserConfigPath);
			}
		}
		catch (Exception ex)
		{
			NLogWrapper.TraceLogger?.Warn(ex, "portable_settings_reset failed path=" + PortableSettingsPath.UserConfigPath);
			WriteFallbackErrorLog("portable_settings_reset failed path=" + PortableSettingsPath.UserConfigPath, ex);
		}
	}

	public void Upgrade(SettingsContext context, SettingsPropertyCollection properties)
	{
	}

	private static string GetSerializedValue(SettingsPropertyValue propertyValue)
	{
		object obj = propertyValue.SerializedValue ?? propertyValue.PropertyValue;
		return obj?.ToString() ?? string.Empty;
	}

	private static bool TrySetXmlValue(XElement valueElement, string serialized)
	{
		if (string.IsNullOrWhiteSpace(serialized))
		{
			return true;
		}
		try
		{
			XElement xElement = XElement.Parse("<root>" + serialized + "</root>");
			valueElement.Add(xElement.Nodes());
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> LoadSettingMap()
	{
		Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)> dictionary = new Dictionary<string, (SettingsSerializeAs serializeAs, string serializedValue)>(StringComparer.Ordinal);
		if (!File.Exists(PortableSettingsPath.UserConfigPath))
		{
			return dictionary;
		}
		try
		{
			XDocument xDocument = XDocument.Load(PortableSettingsPath.UserConfigPath);
			XElement xElement = xDocument.Root?.Element("userSettings")?.Element(SettingsSectionName);
			if (xElement == null)
			{
				return dictionary;
			}
			foreach (XElement item in xElement.Elements("setting"))
			{
				string attributeValue = (string)item.Attribute("name");
				if (string.IsNullOrWhiteSpace(attributeValue))
				{
					continue;
				}
				string text = (string)item.Attribute("serializeAs");
				if (!Enum.TryParse<SettingsSerializeAs>(text, out var result))
				{
					result = SettingsSerializeAs.String;
				}
				XElement xElement2 = item.Element("value");
				string item2 = string.Empty;
				if (xElement2 != null)
				{
					item2 = ((result == SettingsSerializeAs.Xml) ? string.Concat(xElement2.Nodes()) : xElement2.Value);
				}
				dictionary[attributeValue] = (result, item2);
			}
		}
		catch
		{
		}
		return dictionary;
	}

	private static XDocument LoadDocumentForUpdate()
	{
		if (File.Exists(PortableSettingsPath.UserConfigPath))
		{
			try
			{
				return XDocument.Load(PortableSettingsPath.UserConfigPath);
			}
			catch
			{
			}
		}
		XDocument xDocument = new XDocument(new XElement("configuration", new XElement("userSettings", new XElement(SettingsSectionName))));
		return xDocument;
	}

	private static void WriteFallbackErrorLog(string message, Exception ex)
	{
		string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff") + " [ERROR] " + message + Environment.NewLine + ex + Environment.NewLine;
		if (TryAppendText(Path.Combine(PortableSettingsPath.AppBaseDirectory, "application.log"), text))
		{
			return;
		}
		TryAppendText(Path.Combine(Path.GetTempPath(), "BeMusicSeeker-portable-settings.log"), text);
	}

	private static bool TryAppendText(string path, string text)
	{
		try
		{
			string directoryName = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.AppendAllText(path, text, Encoding.UTF8);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
