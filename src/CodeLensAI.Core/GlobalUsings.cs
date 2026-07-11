// Project-wide defaults for Core. CodeLensAI.Shared.Structural is deliberately NOT
// global-imported: it declares MethodInfo/PropertyInfo names that collide with
// CodeLensAI.Shared.Models and must be imported explicitly (usually behind aliases)
// by the files that work with IR types.
global using CodeLensAI.Shared;
global using CodeLensAI.Shared.Interfaces;
global using CodeLensAI.Shared.Models;
global using CodeLensAI.Shared.Utilities;
global using CodeLensAI.Core.AI;
global using CodeLensAI.Core.Analysis;
global using CodeLensAI.Core.Models;
global using CodeLensAI.Core.Parsing;
global using CodeLensAI.Core.Parsing.CSharp;
global using CodeLensAI.Core.Parsing.Cpp;
global using CodeLensAI.Core.Utilities;
