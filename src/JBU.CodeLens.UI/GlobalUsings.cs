// Project-wide defaults for the UI. The UI depends on Shared interfaces and models;
// JBU.CodeLens.Core namespaces are deliberately absent, only the composition root
// (Views/MainWindow.xaml.cs) imports Core, to construct the concrete services.
// JBU.CodeLens.Shared.Structural is imported per-file (its MethodInfo/PropertyInfo names
// collide with JBU.CodeLens.Shared.Models).
global using JBU.CodeLens.Shared;
global using JBU.CodeLens.Shared.Interfaces;
global using JBU.CodeLens.Shared.Models;
global using JBU.CodeLens.Shared.Utilities;
global using JBU.CodeLens.UI.Helpers;
global using JBU.CodeLens.UI.Renderers;
