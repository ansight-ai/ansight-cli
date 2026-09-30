using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;

namespace Ansight.Infrastructure.IOC;

internal sealed class ExternalPartComponentCatalog : ComposablePartCatalog, IExternalPartRegistrar
{
    private const string CreationPolicyKey = "System.ComponentModel.Composition.CreationPolicy";

    private readonly List<ComposablePartDefinition> parts = [];

    public override IQueryable<ComposablePartDefinition> Parts => parts.AsQueryable();

    public void RegisterSingleton<RegisterType, RegisterImplementation>() where RegisterImplementation : RegisterType, new()
    {
        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: () => new RegisterImplementation(),
            displayName: typeof(RegisterImplementation).FullName ?? typeof(RegisterImplementation).Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: true));
    }

    public void RegisterMultiInstance<RegisterType, RegisterImplementation>() where RegisterImplementation : RegisterType, new()
    {
        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: () => new RegisterImplementation(),
            displayName: typeof(RegisterImplementation).FullName ?? typeof(RegisterImplementation).Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: false));
    }

    public void RegisterSingleton<RegisterType>(RegisterType instance)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: () => instance,
            displayName: instance.GetType().FullName ?? instance.GetType().Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: true));
    }

    public void RegisterMultiInstance<RegisterType>(RegisterType instance)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: () => instance,
            displayName: instance.GetType().FullName ?? instance.GetType().Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: false));
    }

    public void RegisterSingleton<RegisterType>(Func<RegisterType> factory)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: factory,
            displayName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: true));
    }

    public void RegisterMultiInstance<RegisterType, RegisterImplementation>(Func<RegisterImplementation> factory)
        where RegisterImplementation : RegisterType
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        parts.Add(new ExternalPartDefinition<RegisterType>(
            partFactory: () => factory(),
            displayName: typeof(RegisterImplementation).FullName ?? typeof(RegisterImplementation).Name,
            contractName: typeof(RegisterType).FullName ?? typeof(RegisterType).Name,
            isSingleton: false));
    }

    private sealed class ExternalPartDefinition<TPart> : ComposablePartDefinition
    {
        private readonly IReadOnlyList<ExportDefinition> exportDefinitions;
        private readonly Dictionary<string, object?> metadata;
        private readonly Func<TPart> partFactory;

        public ExternalPartDefinition(Func<TPart> partFactory, string displayName, string contractName, bool isSingleton)
        {
            if (partFactory is null)
            {
                throw new ArgumentNullException(nameof(partFactory));
            }

            if (isSingleton)
            {
                var singleton = new Lazy<TPart>(partFactory);
                this.partFactory = () => singleton.Value;
            }
            else
            {
                this.partFactory = partFactory;
            }

            DisplayName = displayName;
            ContractName = contractName;
            IsSingleton = isSingleton;

            metadata = new Dictionary<string, object?>
            {
                [CreationPolicyKey] = isSingleton ? CreationPolicy.Shared : CreationPolicy.NonShared
            };

            exportDefinitions =
            [
                new ExportDefinition(contractName, new Dictionary<string, object?>
                {
                    ["ExportTypeIdentity"] = typeof(TPart).FullName ?? typeof(TPart).Name,
                    [CreationPolicyKey] = isSingleton ? CreationPolicy.Shared : CreationPolicy.NonShared
                })
            ];
        }

        public Func<TPart> PartFactory => partFactory;

        public string DisplayName { get; }

        public string ContractName { get; }

        public bool IsSingleton { get; }

        public override IEnumerable<ExportDefinition> ExportDefinitions => exportDefinitions;

        public override IEnumerable<ImportDefinition> ImportDefinitions => Enumerable.Empty<ImportDefinition>();

        public override ComposablePart CreatePart()
        {
            return new ExternalPart<TPart>(PartFactory, exportDefinitions);
        }

        public override IDictionary<string, object?> Metadata => metadata;
    }

    private sealed class ExternalPart<TPart> : ComposablePart
    {
        private readonly IReadOnlyList<ExportDefinition> exportDefinitions;
        private readonly Func<TPart> partFactory;

        public ExternalPart(Func<TPart> partFactory, IReadOnlyList<ExportDefinition> exportDefinitions)
        {
            this.partFactory = partFactory;
            this.exportDefinitions = exportDefinitions;
        }

        public override IEnumerable<ExportDefinition> ExportDefinitions => exportDefinitions;

        public override IEnumerable<ImportDefinition> ImportDefinitions => Enumerable.Empty<ImportDefinition>();

        public override object GetExportedValue(ExportDefinition definition)
        {
            return partFactory()!;
        }

        public override void SetImport(ImportDefinition definition, IEnumerable<Export> exports)
        {
            // External parts do not support imports.
        }
    }
}
