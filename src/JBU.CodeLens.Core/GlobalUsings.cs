// Project-wide defaults for Core. JBU.CodeLens.Shared.Structural is deliberately NOT
// global-imported: it declares MethodInfo/PropertyInfo names that collide with
// JBU.CodeLens.Shared.Models and must be imported explicitly (usually behind aliases)
// by the files that work with IR types.
global using JBU.CodeLens.Shared;
global using JBU.CodeLens.Shared.Interfaces;
global using JBU.CodeLens.Shared.Models;
global using JBU.CodeLens.Shared.Utilities;
global using JBU.CodeLens.Core.AI;
global using JBU.CodeLens.Core.Analysis;
global using JBU.CodeLens.Core.Models;
global using JBU.CodeLens.Core.Parsing;
global using JBU.CodeLens.Core.Parsing.CSharp;
global using JBU.CodeLens.Core.Parsing.Cpp;
global using JBU.CodeLens.Core.Utilities;
