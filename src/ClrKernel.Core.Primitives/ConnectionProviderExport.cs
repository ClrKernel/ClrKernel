using System;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// Marks an assembly as exporting a connection-provider descriptor. When a
/// session loads the assembly (<c>#r "nuget: ClrKernel.Database.Provider.Oracle"</c>),
/// the engine reads the named type's static <c>Descriptor</c> property and adds it
/// to that session's provider list, so connection UIs can describe the provider.
/// A descriptor whose <see cref="ConnectionProviderDescriptor.Type"/> is already
/// registered is skipped. Lives in Core.Primitives so provider packages need no
/// reference to the scripting stack.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ConnectionProviderExportAttribute : Attribute {
    /// <param name="descriptorSource">A type with a static
    /// <c>ConnectionProviderDescriptor Descriptor { get; }</c> property.</param>
    public ConnectionProviderExportAttribute(Type descriptorSource) {
        DescriptorSource = descriptorSource;
    }

    public Type DescriptorSource { get; }
}
