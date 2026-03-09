using System;
using System.Reflection;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSTableLoadTests
{
    [TestMethod]
    public void LoadHeaderJson_StoresPassedHeaderUri()
    {
        BMSTable table = new BMSTable();
        Uri pageUri = new Uri("https://example.com/table.html");
        Uri headerUri = new Uri("https://example.com/header.json");

        table.LoadHeaderJSON("{\"name\":\"Test\",\"symbol\":\"T\",\"data_url\":\"score.json\",\"level_order\":[1]}", pageUri, headerUri);

        Assert.AreEqual(pageUri, table.Page_url);
        Assert.AreEqual(headerUri, table.Header_url);
        Assert.AreEqual("score.json", table.data_url);
    }

    [TestMethod]
    public void LoadHeaderJson_PreservesInnerException()
    {
        BMSTable table = new BMSTable();

        PlaylistHeaderParseException exception = Assert.ThrowsException<PlaylistHeaderParseException>(() => table.LoadHeaderJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadDataJson_PreservesInnerException()
    {
        BMSTable table = new BMSTable();

        PlaylistDataParseException exception = Assert.ThrowsException<PlaylistDataParseException>(() => table.LoadDataJSON("{ invalid json }"));

        Assert.IsNotNull(exception.InnerException);
    }

    [TestMethod]
    public void LoadHeaderJson_InvalidDataUrlThrowsInvalidOperationException()
    {
        BMSTable table = new BMSTable();

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() => table.LoadHeaderJSON("{\"name\":\"Test\",\"symbol\":\"T\",\"data_url\":\"http://[broken\",\"level_order\":[1]}"));

        StringAssert.Contains(exception.Message, "Failed to resolve playlist data_url");
    }

    [TestMethod]
    public void StoredDataUrlSetter_InvalidValueDoesNotThrowAndKeepsNull()
    {
        BMSTable table = new BMSTable();
        PropertyInfo propertyInfo = typeof(BMSTable).GetProperty("data_url", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        propertyInfo.SetValue(table, "http://[broken");

        Assert.IsNull(table.Data_url);
    }
}
