// Project-wide defaults for the UI. The UI depends on Shared interfaces and models;
// CodeLensAI.Core namespaces are deliberately absent — only the composition root
// (Views/MainWindow.xaml.cs) imports Core, to construct the concrete services.
// CodeLensAI.Shared.Structural is imported per-file (its MethodInfo/PropertyInfo names
// collide with CodeLensAI.Shared.Models).
global using CodeLensAI.Shared;
global using CodeLensAI.Shared.Interfaces;
global using CodeLensAI.Shared.Models;
global using CodeLensAI.Shared.Utilities;
global using CodeLensAI.UI.Helpers;
global using CodeLensAI.UI.Renderers;
