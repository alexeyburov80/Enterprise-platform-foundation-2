namespace Dp.Platform.Stub.OneC;

/// <summary>Минимальный EDMX-ответ $metadata имитатора 1С (достаточно для демо).</summary>
public static class OdataMetadata
{
    public const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="StandardODATA">
              <EntityType Name="Document_SalesOrder">
                <Key><PropertyRef Name="Ref_Key"/></Key>
                <Property Name="Ref_Key" Type="Edm.Guid" Nullable="false"/>
                <Property Name="Number" Type="Edm.String"/>
                <Property Name="Date" Type="Edm.DateTimeOffset"/>
                <Property Name="Posted" Type="Edm.Boolean"/>
                <Property Name="Counterparty" Type="Edm.String"/>
                <Property Name="Amount" Type="Edm.Decimal"/>
                <Property Name="Currency" Type="Edm.String"/>
                <Property Name="Comment" Type="Edm.String"/>
              </EntityType>
              <EntityContainer Name="StandardODATA_EntityData">
                <EntitySet Name="Document_SalesOrder" EntityType="StandardODATA.Document_SalesOrder"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;
}
