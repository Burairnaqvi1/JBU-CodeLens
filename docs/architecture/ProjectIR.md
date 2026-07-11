# ProjectIR — Intermediate Representation

`ProjectIR` is the structured metadata model produced by parsing and consumed by analysis, graph, LLM, and export modules. It contains no source code — only structural information.

## Root: ProjectIR

```csharp
public class ProjectIR
{
    public string ProjectName { get; set; }
    public List<NamespaceInfo> Namespaces { get; set; }   // each namespace
    public List<Relationship> Relationships { get; set; } // inter-type edges
}
```

## NamespaceInfo

| Property | Type | Description |
|---|---|---|
| Name | string | Fully qualified namespace name |
| Classes | List\<TypeInfo\> | Class types in this namespace |
| Interfaces | List\<TypeInfo\> | Interface types in this namespace |
| Structs | List\<TypeInfo\> | Struct types in this namespace |
| Enums | List\<TypeInfo\> | Enum types in this namespace |

## TypeInfo

| Property | Type | Description |
|---|---|---|
| Name | string | Type name (short) |
| FullName | string | Fully qualified name (e.g. `MyApp.Services.AuthService`) |
| Kind | TypeKind | Class, Interface, Struct, or Enum |
| BaseType | string? | Base class full name |
| Interfaces | List\<string\> | Implemented interface full names |
| Methods | List\<MethodInfo\> | Methods declared in this type |
| Properties | List\<PropertyInfo\> | Properties declared in this type |
| Fields | List\<FieldInfo\> | Fields declared in this type |
| XmlDoc | string? | XML documentation comment text |

## MethodInfo

| Property | Type | Description |
|---|---|---|
| Name | string | Method name |
| ReturnType | string | Return type name |
| Parameters | List\<ParameterInfo\> | Method parameters |
| Modifiers | string | Access modifiers (e.g. `public static`) |
| CyclomaticComplexity | int | Cyclomatic complexity count |
| XmlDoc | string? | XML documentation comment text |
| Calls | List\<string\> | Full names of called methods |

## PropertyInfo / FieldInfo

| Property | Type | Description |
|---|---|---|
| Name | string | Member name |
| Type | string | Member type name |
| Modifiers | string | Access modifiers |

## Relationship

| Property | Type | Description |
|---|---|---|
| Source | string | Source type full name |
| Target | string | Target type full name |
| Kind | RelationKind | CALLS, INHERITS, IMPLEMENTS, CONTAINS |

## MetricsResult

| Property | Type | Description |
|---|---|---|
| TotalClasses | int | Count |
| TotalMethods | int | Count |
| TotalNamespaces | int | Count |
| TotalRelationships | int | Count |
| AverageComplexity | double | Mean cyclomatic complexity |
| AverageCoupling | double | Mean coupling score |
| MaxInheritanceDepth | int | Deepest inheritance chain |
| MaintainabilityIndex | double | MI score (higher = more maintainable) |

## Edge Types (Knowledge Graph)

| Edge Kind | Meaning |
|---|---|
| CONTAINS | Namespace contains a type |
| INHERITS | Class extends another class |
| IMPLEMENTS | Class implements an interface |
| CALLS | Method calls another method |
