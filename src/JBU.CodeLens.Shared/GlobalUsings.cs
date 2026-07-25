// Shared-internal defaults. Shared.Structural is deliberately NOT global-imported anywhere:
// it declares MethodInfo/PropertyInfo names that collide with Shared.Models and must be
// imported explicitly (usually behind aliases) by files that need IR types.
global using JBU.CodeLens.Shared.Models;
global using JBU.CodeLens.Shared.Utilities;
