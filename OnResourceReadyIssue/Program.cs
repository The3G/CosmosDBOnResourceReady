using Aspire.Hosting.Azure;

using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Storage;

using Bogus;

using Microsoft.Azure.Cosmos;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System.ComponentModel.DataAnnotations;


var builder = DistributedApplication.CreateBuilder(args);

/*: generate fake data using import files and bogus to populate database and blob storage */
var fake = new Faker<ResourceItem>()
    .RuleFor(i => i.Title, f => f.Lorem.Sentence(3))
    .RuleFor(i => i.Description, f => f.Lorem.Paragraph(2))
    .RuleFor(i => i.Tags, f => f.Lorem.Words(3).ToArray())
    .RuleFor(i => i.Sensitivity, f => f.PickRandom<ResourceSensitivity>())
    .RuleFor(i => i.References,
        f => f.Make(f.Random.Int(1, 3), () => new ResourceHyperlink
        {
            Hyperlink = new Uri(f.Internet.Url()),
            Title = f.Lorem.Sentence(2),
            Description = f.Lorem.Paragraph(1),
            IsExternal = f.Random.Bool(),
            IsTrusted = f.Random.Bool()
        })
    )
    .RuleFor(i => i.ImportedBy, f => f.Person.FullName)
    .RuleFor(i => i.ImportedOn, f => f.Date.PastOffset())
    .RuleFor(i => i.PublishedBy, f => f.Person.FullName)
    .RuleFor(i => i.PublishedOn, f => f.Date.BetweenOffset(
        f.Date.Past(1), f.Date.FutureOffset(1))
    );


var cosmosdb    = builder.AddAzureCosmosDB(name: "azcosmos")
    .ConfigureInfrastructure(infra => 
    {
        var resource = infra.GetProvisionableResources()
            .OfType<CosmosDBAccount>()
            .FirstOrDefault(r => r.Name.Equals("azcosmos"));
    });
var storage     = builder.AddAzureStorage(name: "azstorage")
    .ConfigureInfrastructure(infra => 
    {
        var resource = infra.GetProvisionableResources()
            .OfType<StorageAccount>()
            .FirstOrDefault(r => r.Name.Equals("azstorage"));
        resource.Sku.Name = StorageSkuName.StandardLrs;
        resource.AccessTier = StorageAccountAccessTier.Hot;
        
        resource.AllowCrossTenantReplication = false;
        resource.EnableHttpsTrafficOnly = true;
        
        resource.IsNfsV3Enabled=false;
        resource.IsSftpEnabled = false;

        resource.PublicNetworkAccess = 
            StoragePublicNetworkAccess.Disabled;
        resource.AllowBlobPublicAccess = false;
    });

var appImport = cosmosdb.AddCosmosDatabase(name: "appimport");
var cdbImport = appImport.AddContainer(name: "cdbimport",   partitionKeyPath:   "/filePath");
var sabImport = storage.AddBlobContainer(name:"sabimport",      blobContainerName:  "sabimport");
var saqImport = storage.AddQueue(name:"saqimport",              queueName:          "saqimport");

/*: resource events to import the data from the directory */
cdbImport.OnResourceReady(DatabaseEntityImportFromDirectory);
sabImport.OnResourceReady(BlobContentImportFromDirectory);
saqImport.OnResourceReady(QueueMessageImportFromDirectory);

/*: */
cosmosdb.RunAsEmulator();
storage.RunAsEmulator();

builder.Build()
    .Run();

async Task DatabaseEntityImportFromDirectory(
    AzureCosmosDBContainerResource resource,
    ResourceReadyEvent resourceReadyEvent,
    CancellationToken cancellationToken)
{
    var hostEnvironment = resourceReadyEvent.Services
            .GetRequiredService<IHostEnvironment>();
    var logger = resourceReadyEvent.Services
        .GetRequiredService<ILogger<AzureCosmosDBContainerResource>>();

    string applicationImportPath = Path.Combine(
            hostEnvironment.ContentRootPath.ToLower(),
            hostEnvironment.EnvironmentName.ToLower(),
            "import"
        );

    if (!Directory.Exists(applicationImportPath))
        Directory.CreateDirectory(applicationImportPath);

    var resourceConnectionString = await resource
        .Parent.Parent
        .ConnectionStringExpression
        .GetValueAsync(cancellationToken)
        .ConfigureAwait(false);
    
    using(var scoped = logger.BeginScope("importing data from directory"))
    {
        logger.LogInformation("importing data from directory: {Directory}", 
            applicationImportPath);
        try 
        {
            logger.LogInformation("connecting: {connectionString}",
                resourceConnectionString);
            CosmosClient resourceClient = new CosmosClient(
                resourceConnectionString, new CosmosClientOptions
                {
                    LimitToEndpoint = resource.IsEmulator(),
                    ServerCertificateCustomValidationCallback = (cert,chain,policy) =>
                    {
                        // bypass certificate validation in emulator
                        return true;
                    },
                    ConsistencyLevel = ConsistencyLevel.Eventual,
                    ConnectionMode = resource.IsEmulator() ? 
                        ConnectionMode.Gateway : ConnectionMode.Direct
                });
            logger.LogInformation("creating if not exists: {databaseName}",
                resource.Parent.DatabaseName);
            await resourceClient.CreateDatabaseIfNotExistsAsync(
                resource.Parent.DatabaseName);
            logger.LogInformation("getting database: {databaseName}",
                resource.Parent.DatabaseName);
            var resourceDatabase = resourceClient.GetDatabase(
                resource.Parent.DatabaseName);
            logger.LogInformation("creating container: {containerName}",
                resource.ContainerName);
            await resourceDatabase.CreateContainerIfNotExistsAsync(
                resource.ContainerName, resource.PartitionKeyPath);
            var resourceContainer = resourceDatabase.GetContainer(
                resource.ContainerName);
            var importItems = fake.GenerateLazy(10)
                .Select(item => new 
                {
                    id = Guid.NewGuid().ToString(),
                    item.Title,
                    item.Description,
                    item.Tags,
                    item.Sensitivity,
                    item.References,
                    item.ImportedBy,
                    item.ImportedOn,
                    item.PublishedBy,
                    item.PublishedOn,
                    filePath = applicationImportPath
                });

            foreach(var item in importItems)
            {
                logger.LogInformation("importing item: {Title}", item.Title);
                await resourceContainer.CreateItemAsync(
                    item,
                    new PartitionKey(applicationImportPath),
                    cancellationToken: cancellationToken
                );
            }

        }
        catch(Exception ex)
        {
            logger.LogInformation("exception: {errorMessage}",
                ex.Message);
        }
        finally
        {

        }
    }

    await Task.CompletedTask;
}
async Task BlobContentImportFromDirectory(
    AzureBlobStorageContainerResource resource,
    ResourceReadyEvent resourceReadyEvent,
    CancellationToken cancellationToken)
{
    await Task.CompletedTask;
}
async Task QueueMessageImportFromDirectory(
    AzureQueueStorageQueueResource resource,
    ResourceReadyEvent resourceReadyEvent,
    CancellationToken cancellationToken)
{
    await Task.CompletedTask;
}

/// <summary>
/// </summary>
public class ResourceItem
{
    /// <summary>
    /// </summary>
    [StringLength(maximumLength: 100)]
    [Required(AllowEmptyStrings = false)]
    public string                   Title             { get; set; }

    /// <summary>
    /// </summary>
    [StringLength(maximumLength: 800)]
    public string?                  Description       { get; set; }

    /// <summary>
    /// Tags associated with the import item, 
    /// useful for categorization.
    /// </summary>
    public string[]                 Tags            { get; set; }
        = new string[] { };

    /// <summary>
    /// </summary>
    public ResourceSensitivity      Sensitivity     { get; set; }
        = ResourceSensitivity.Public;

    /// <summary>
    /// </summary>
    public IList<ResourceHyperlink> References      { get; set; }
        = new List<ResourceHyperlink>();

    #region Import Information
    /// <summary>
    /// </summary>
    public string           ImportedBy   { get; set; }
    /// <summary>
    /// </summary>
    public DateTimeOffset   ImportedOn   { get; set; }
    #endregion

    #region Publishing Information
    /// <summary>
    /// </summary>
    public string?         PublishedBy  { get; set; }
        = null;
    
    /// <summary>
    /// </summary>
    public DateTimeOffset? PublishedOn  { get; set; } 
        = null;
    #endregion
}

/// <summary>
/// </summary>
public class ResourceHyperlink
{
    public Uri      Hyperlink       { get; set; }
    
    public string   Title           { get; set; }
    
    public string?  Description     { get; set; }
        = string.Empty;

    public bool?    IsExternal      { get; set; }
        = true;
    public bool?    IsTrusted       { get; set; } 
        = false;
}

/// <summary>
/// </summary>
public enum ResourceSensitivity
{
    /// <summary>
    /// Public is non-proprietary business information, 
    /// or information obtained from the public domain, 
    /// which is not expected to cause damage to business 
    /// activities or competitive status.
    /// 
    /// Public Information may be suitable for public 
    /// dissemination. Be sure to follow the procedure
    /// set up by marketing & communications before
    /// releasing sensitive Information to the public. 
    /// </summary>
    Public,
    /// <summary>
    /// Internal is confidential and proprietary business information,
    /// and its unauthorized disclosure could cause damage to business
    /// activities or competitive status. Such information requires 
    /// careful consideration before sharing; it should also be protected
    /// from unauthorized modification and disclosure. 
    /// 
    /// Internal information is intended for widespread distribution 
    /// within the company, but not for persons outside the company. 
    /// </summary>
    Internal,

    /// <summary>
    /// Confidential and proprietary business information and its 
    /// unauthorized disclosure could be expected to cause significant 
    /// damage to the business activities or competitive status. 
    /// 
    /// Confidential information warrants heightened protective measures 
    /// to safeguard it against intended or accidental loss, attack or 
    /// unauthorized access.
    /// 
    /// Confidential information may be shared with individuals with a 
    /// legitimate business need to know, but should not be widely shared
    /// inside or outside the company. 
    /// </summary>
    Confidential,

    /// <summary>
    /// Restricted is confidential and proprietary business Information, 
    /// and its unauthorized disclosure could be expected to cause 
    /// catastrophic damage to DXC’s business activities or competitive 
    /// status. DXC Restricted Information warrants the highest levels of 
    /// protection from malicious or accidental loss, attack or unauthorized
    /// access. “DXC Restricted Information” shall not be shared without a 
    /// strict need to know and only when necessary protections are in place.
    /// </summary>
    Restricted,
}