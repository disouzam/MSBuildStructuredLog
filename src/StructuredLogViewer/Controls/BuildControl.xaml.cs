﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.Controls
{
    public partial class BuildControl : UserControl
    {
        public Build Build { get; set; }
        public TreeViewItem SelectedTreeViewItem { get; private set; }
        public string LogFilePath { get; private set; }

        private ScrollViewer scrollViewer;

        private SourceFileResolver sourceFileResolver;
        private ArchiveFileResolver archiveFile => sourceFileResolver.ArchiveFile;
        private PreprocessedFileManager preprocessedFileManager;

        private MenuItem copyItem;
        private MenuItem copySubtreeItem;
        private MenuItem copyNameItem;
        private MenuItem copyValueItem;
        private MenuItem viewItem;
        private MenuItem preprocessItem;
        private MenuItem hideItem;
        private MenuItem copyAllItem;
        private ContextMenu sharedTreeContextMenu;

        public TreeView ActiveTreeView;

        public BuildControl(Build build, string logFilePath)
        {
            InitializeComponent();

            searchLogControl.WatermarkText = @"Type in the search box to search. Search for multiple words separated by space (space means AND). Results (up to 500) will display here.


Use syntax like '$property Prop' to narrow results down by item kind (supported kinds: $project, $target, $task, $error, $warning, $message, $property, $item, $additem, $removeitem, $metadata)";

            searchLogControl.ExecuteSearch = searchText =>
            {
                var search = new Search(Build);
                var results = search.FindNodes(searchText);
                return results;
            };
            searchLogControl.ResultsTreeBuilder = BuildResultTree;

            findInFilesControl.ExecuteSearch = FindInFiles;
            findInFilesControl.ResultsTreeBuilder = BuildFindResults;

            VirtualizingPanel.SetIsVirtualizing(treeView, SettingsService.EnableTreeViewVirtualization);

            DataContext = build;
            Build = build;

            LogFilePath = logFilePath;

            if (build.SourceFilesArchive != null)
            {
                // first try to see if the source archive was embedded in the log
                sourceFileResolver = new SourceFileResolver(build.SourceFilesArchive);
            }
            else
            {
                // otherwise try to read from the .zip file on disk if present
                sourceFileResolver = new SourceFileResolver(logFilePath);
            }

            sharedTreeContextMenu = new ContextMenu();
            copyAllItem = new MenuItem() { Header = "Copy All" };
            copyAllItem.Click += (s, a) => CopyAll();
            sharedTreeContextMenu.Items.Add(copyAllItem);

            var contextMenu = new ContextMenu();
            contextMenu.Opened += ContextMenu_Opened;
            copyItem = new MenuItem() { Header = "Copy" };
            copySubtreeItem = new MenuItem() { Header = "Copy subtree" };
            copyNameItem = new MenuItem() { Header = "Copy name" };
            copyValueItem = new MenuItem() { Header = "Copy value" };
            viewItem = new MenuItem() { Header = "View" };
            preprocessItem = new MenuItem() { Header = "Preprocess" };
            hideItem = new MenuItem() { Header = "Hide" };
            copyItem.Click += (s, a) => Copy();
            copySubtreeItem.Click += (s, a) => CopySubtree();
            copyNameItem.Click += (s, a) => CopyName();
            copyValueItem.Click += (s, a) => CopyValue();
            viewItem.Click += (s, a) => Invoke(treeView.SelectedItem as ParentedNode);
            preprocessItem.Click += (s, a) => Preprocess(treeView.SelectedItem as Project);
            hideItem.Click += (s, a) => Delete();
            contextMenu.Items.Add(viewItem);
            contextMenu.Items.Add(preprocessItem);
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(copySubtreeItem);
            contextMenu.Items.Add(copyNameItem);
            contextMenu.Items.Add(copyValueItem);
            contextMenu.Items.Add(hideItem);

            var existingTreeViewItemStyle = (Style)Application.Current.Resources[typeof(TreeViewItem)];
            var treeViewItemStyle = new Style(typeof(TreeViewItem), existingTreeViewItemStyle);
            treeViewItemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            treeViewItemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
            treeViewItemStyle.Setters.Add(new Setter(TreeViewItem.VisibilityProperty, new Binding("IsVisible") { Mode = BindingMode.TwoWay, Converter = new BooleanToVisibilityConverter() }));
            treeViewItemStyle.Setters.Add(new EventSetter(MouseDoubleClickEvent, (MouseButtonEventHandler)OnItemDoubleClick));
            treeViewItemStyle.Setters.Add(new EventSetter(PreviewMouseRightButtonDownEvent, (MouseButtonEventHandler)OnPreviewMouseRightButtonDown));
            treeViewItemStyle.Setters.Add(new EventSetter(RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)TreeViewItem_RequestBringIntoView));
            treeViewItemStyle.Setters.Add(new EventSetter(KeyDownEvent, (KeyEventHandler)OnItemKeyDown));

            treeView.ContextMenu = contextMenu;
            treeView.ItemContainerStyle = treeViewItemStyle;
            treeView.KeyDown += TreeView_KeyDown;
            treeView.SelectedItemChanged += TreeView_SelectedItemChanged;
            treeView.GotFocus += (s, a) => ActiveTreeView = treeView;

            searchLogControl.ResultsList.ItemContainerStyle = treeViewItemStyle;
            searchLogControl.ResultsList.SelectedItemChanged += ResultsList_SelectionChanged;
            searchLogControl.ResultsList.GotFocus += (s, a) => ActiveTreeView = searchLogControl.ResultsList;
            searchLogControl.ResultsList.ContextMenu = sharedTreeContextMenu;

            findInFilesControl.GotFocus += (s, a) => ActiveTreeView = findInFilesControl.ResultsList;
            findInFilesControl.ResultsList.ItemContainerStyle = treeViewItemStyle;
            findInFilesControl.ResultsList.GotFocus += (s, a) => ActiveTreeView = findInFilesControl.ResultsList;
            findInFilesControl.ResultsList.ContextMenu = sharedTreeContextMenu;

            if (archiveFile != null)
            {
                filesTab.Visibility = Visibility.Visible;
                findInFilesTab.Visibility = Visibility.Visible;
                PopulateFilesTab();
                filesTree.ItemContainerStyle = treeViewItemStyle;
            }

            breadCrumb.SelectionChanged += BreadCrumb_SelectionChanged;

            Loaded += BuildControl_Loaded;

            preprocessedFileManager = new PreprocessedFileManager(this, sourceFileResolver);
        }

        private void Preprocess(Project project)
        {
            preprocessedFileManager.ShowPreprocessed(project.SourceFilePath);
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var node = treeView.SelectedItem as ParentedNode;
            var visibility = node is NameValueNode ? Visibility.Visible : Visibility.Collapsed;
            copyNameItem.Visibility = visibility;
            copyValueItem.Visibility = visibility;
            viewItem.Visibility = CanView(node) ? Visibility.Visible : Visibility.Collapsed;
            copySubtreeItem.Visibility = node is TreeNode t && t.HasChildren ? Visibility.Visible : Visibility.Collapsed;
            preprocessItem.Visibility = node is Project p && preprocessedFileManager.CanPreprocess(p.SourceFilePath) ? Visibility.Visible : Visibility.Collapsed;
        }

        private object FindInFiles(string searchText)
        {
            var results = new List<(string, IEnumerable<(int, string)>)>();

            foreach (var file in archiveFile.Files)
            {
                var haystack = file.Value;
                var resultsInFile = haystack.Find(searchText);
                if (resultsInFile.Count > 0)
                {
                    results.Add((file.Key, resultsInFile.Select(lineNumber => (lineNumber, haystack.GetLineText(lineNumber)))));
                }
            }

            return results;
        }

        private IEnumerable BuildFindResults(object resultsObject)
        {
            if (resultsObject == null)
            {
                return null;
            }

            var results = resultsObject as IEnumerable<(string, IEnumerable<(int, string)>)>;

            var root = new Folder();

            // root.Children.Add(new Message { Text = "Elapsed " + Elapsed.ToString() });

            if (results != null)
            {
                foreach (var file in results)
                {
                    var folder = new SourceFile()
                    {
                        Name = Path.GetFileName(file.Item1),
                        SourceFilePath = file.Item1,
                        IsExpanded = true
                    };
                    root.AddChild(folder);
                    foreach (var line in file.Item2)
                    {
                        var sourceFileLine = new SourceFileLine()
                        {
                            LineNumber = line.Item1 + 1,
                            LineText = line.Item2
                        };
                        folder.AddChild(sourceFileLine);
                    }
                }
            }

            if (!root.HasChildren && !string.IsNullOrEmpty(findInFilesControl.SearchText))
            {
                root.Children.Add(new Message { Text = "No results found." });
            }

            return root.Children;
        }

        private void PopulateFilesTab()
        {
            var root = new Folder();

            foreach (var file in archiveFile.Files.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                var parts = file.Key.Split('\\');
                AddSourceFile(root, file.Key, parts, 0);
            }

            foreach (var subFolder in root.Children.OfType<Folder>())
            {
                CompressTree(subFolder);
            }

            filesTree.ItemsSource = root.Children;
            filesTree.GotFocus += (s, a) => ActiveTreeView = filesTree;
            filesTree.ContextMenu = sharedTreeContextMenu;
        }

        private void CompressTree(Folder parent)
        {
            if (parent.Children.Count == 1 && parent.Children[0] is Folder subfolder)
            {
                parent.Children.Clear();
                var grandchildren = subfolder.Children.ToArray();
                subfolder.Children.Clear();
                foreach (var grandChild in grandchildren)
                {
                    parent.Children.Add(grandChild);
                }

                parent.Name = Path.Combine(parent.Name, subfolder.Name);
                CompressTree(parent);
            }
            else
            {
                foreach (var subFolder in parent.Children.OfType<Folder>())
                {
                    CompressTree(subFolder);
                }
            }
        }

        private void AddSourceFile(Folder folder, string filePath, string[] parts, int index)
        {
            if (index == parts.Length - 1)
            {
                var file = new SourceFile()
                {
                    SourceFilePath = filePath,
                    Name = parts[index]
                };
                folder.AddChild(file);
            }
            else
            {
                var subfolder = folder.GetOrCreateNodeWithName<Folder>(parts[index]);
                subfolder.IsExpanded = true;
                AddSourceFile(subfolder, filePath, parts, index + 1);
            }
        }

        /// <summary>
        /// This is needed as a workaround for a weird bug. When the breadcrumb spans multiple lines
        /// and we click on an item on the first line, it truncates the breadcrumb up to that item.
        /// The fact that the breadcrumb moves down while the Mouse is captured results in a MouseMove
        /// in the ListBox, which triggers moving selection to top and selecting the first item.
        /// Without this "reentrancy" guard the event would be handled twice, with just the root
        /// of the chain left in the breadcrumb at the end.
        /// </summary>
        private bool isProcessingBreadcrumbClick = false;
        internal static TimeSpan Elapsed;

        private void BreadCrumb_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isProcessingBreadcrumbClick)
            {
                return;
            }

            isProcessingBreadcrumbClick = true;
            var node = breadCrumb.SelectedItem as TreeNode;
            if (node != null)
            {
                SelectItem(node);
                treeView.Focus();
                e.Handled = true;
            }

            // turn it off only after the storm of layouts caused by the mouse click has subsided
            Dispatcher.InvokeAsync(() => { isProcessingBreadcrumbClick = false; }, DispatcherPriority.Background);
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var item = treeView.SelectedItem;
            if (item != null)
            {
                UpdateBreadcrumb(item);
            }
        }

        private void ResultsList_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var proxy = searchLogControl.ResultsList.SelectedItem as ProxyNode;
            if (proxy != null)
            {
                var item = proxy.Original as ParentedNode;
                if (item != null)
                {
                    SelectItem(item);
                }
            }
        }

        public void UpdateBreadcrumb(object item)
        {
            var parentedNode = item as ParentedNode;
            IEnumerable<object> chain = parentedNode.GetParentChain();
            if (chain == null || !chain.Any())
            {
                chain = new[] { item };
            }
            else
            {
                chain = IntersperseWithSeparators(chain).ToArray();
            }

            breadCrumb.ItemsSource = chain;
            breadCrumb.SelectedIndex = -1;
        }

        private IEnumerable<object> IntersperseWithSeparators(IEnumerable<object> list)
        {
            bool first = true;
            foreach (var item in list)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    yield return new Separator();
                }

                yield return item;
            }
        }

        private void BuildControl_Loaded(object sender, RoutedEventArgs e)
        {
            scrollViewer = treeView.Template.FindName("_tv_scrollviewer_", treeView) as ScrollViewer;

            if (!Build.Succeeded)
            {
                var firstError = Build.FindFirstInSubtreeIncludingSelf<Error>();
                if (firstError != null)
                {
                    SelectItem(firstError);
                    treeView.Focus();
                }

                searchLogControl.SearchText = "$error";
            }
        }

        private void SelectItem(ParentedNode item)
        {
            var parentChain = item.GetParentChain();
            if (!parentChain.Any())
            {
                return;
            }

            treeView.SelectContainerFromItem<object>(parentChain);
        }

        private void TreeView_KeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Delete)
            {
                Delete();
                args.Handled = true;
            }
            else if (args.Key == Key.C && args.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                CopySubtree();
                args.Handled = true;
            }
        }

        public void Delete()
        {
            var node = treeView.SelectedItem as TreeNode;
            if (node != null)
            {
                MoveSelectionOut(node);
                node.IsVisible = false;
            }
        }

        public void Copy()
        {
            var treeNode = treeView.SelectedItem;
            if (treeNode != null)
            {
                var text = treeNode.ToString();
                CopyToClipboard(text);
            }
        }

        public void CopySubtree()
        {
            var treeNode = treeView.SelectedItem;
            if (treeNode != null)
            {
                var text = Microsoft.Build.Logging.StructuredLogger.StringWriter.GetString(treeNode);
                CopyToClipboard(text);
            }
        }

        private void CopyAll()
        {
            var tree = ActiveTreeView;
            if (tree == null)
            {
                return;
            }

            var sb = new StringBuilder();
            foreach (var item in tree.Items)
            {
                var text = Microsoft.Build.Logging.StructuredLogger.StringWriter.GetString(item);
                sb.AppendLine(text);
            }

            CopyToClipboard(sb.ToString());
        }

        private static void CopyToClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
                // clipboard API is notoriously flaky
            }
        }

        public void CopyName()
        {
            var nameValueNode = treeView.SelectedItem as NameValueNode;
            if (nameValueNode != null)
            {
                CopyToClipboard(nameValueNode.Name);
            }
        }

        public void CopyValue()
        {
            var nameValueNode = treeView.SelectedItem as NameValueNode;
            if (nameValueNode != null)
            {
                CopyToClipboard(nameValueNode.Value);
            }
        }

        private void MoveSelectionOut(ParentedNode node)
        {
            var parent = node.Parent;
            if (parent == null)
            {
                return;
            }

            var next = parent.FindNextChild<BaseNode>(node);
            if (next != null)
            {
                node.IsSelected = false;
                next.IsSelected = true;
                return;
            }

            var previous = parent.FindPreviousChild<BaseNode>(node);
            if (previous != null)
            {
                node.IsSelected = false;
                previous.IsSelected = true;
            }
            else
            {
                node.IsSelected = false;
                parent.IsSelected = true;
            }
        }

        private void OnItemKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Space || args.Key == Key.Return)
            {
                var treeNode = GetNode(args);
                if (treeNode != null)
                {
                    args.Handled = Invoke(treeNode);
                }
            }

            if (args.Key == Key.Escape)
            {
                if (documentWell.IsVisible)
                {
                    documentWell.Hide();
                }
            }
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs args)
        {
            // workaround for http://stackoverflow.com/a/36244243/37899
            var treeViewItem = sender as TreeViewItem;
            if (!treeViewItem.IsSelected)
            {
                return;
            }

            var node = GetNode(args);
            if (node != null)
            {
                args.Handled = Invoke(node);
            }
        }

        private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs args)
        {
            var treeViewItem = sender as TreeViewItem;
            if (treeViewItem != null)
            {
                treeViewItem.IsSelected = true;
            }
        }

        private bool CanView(ParentedNode node)
        {
            return node is AbstractDiagnostic
                || node is Project
                || (node is Target t && t.SourceFilePath != null && sourceFileResolver.HasFile(t.SourceFilePath))
                || (node is Task task && task.Parent is Target parentTarget && sourceFileResolver.HasFile(parentTarget.SourceFilePath))
                || (node is IHasSourceFile ihsf && ihsf.SourceFilePath != null && sourceFileResolver.HasFile(ihsf.SourceFilePath))
                || (node is NameValueNode nvn && nvn.IsValueShortened)
                || (node is TextNode tn && tn.IsTextShortened);
        }

        private bool Invoke(ParentedNode treeNode)
        {
            if (treeNode == null)
            {
                return false;
            }

            try
            {
                switch (treeNode)
                {
                    case AbstractDiagnostic diagnostic:
                        var path = diagnostic.File;
                        if (!DisplayFile(path, diagnostic.LineNumber) &&
                            path != null &&
                            !Path.IsPathRooted(path) &&
                            diagnostic.ProjectFile != null)
                        {
                            // path must be relative, try to normalize:
                            path = Path.Combine(Path.GetDirectoryName(diagnostic.ProjectFile), path);
                            return DisplayFile(path, diagnostic.LineNumber, diagnostic.ColumnNumber);
                        }

                        break;
                    case Target target:
                        return DisplayTarget(target.SourceFilePath, target.Name);
                    case Task task:
                        return DisplayTask(task.SourceFilePath, task.Parent, task.Name);
                    case IHasSourceFile hasSourceFile:
                        int line = 0;
                        var hasLine = hasSourceFile as IHasLineNumber;
                        if (hasLine != null)
                        {
                            line = hasLine.LineNumber ?? 0;
                        }

                        return DisplayFile(hasSourceFile.SourceFilePath, line);
                    case SourceFileLine sourceFileLine:
                        var file = sourceFileLine.Parent as SourceFile;
                        if (file != null)
                        {
                            DisplayFile(file.SourceFilePath, sourceFileLine.LineNumber);
                            return true;
                        }

                        return false;
                    case NameValueNode nameValueNode:
                        if (nameValueNode.IsValueShortened)
                        {
                            return DisplayText(nameValueNode.Value, nameValueNode.Name);
                        }

                        return false;
                    case TextNode textNode:
                        if (textNode.IsTextShortened)
                        {
                            return DisplayText(textNode.Text, textNode.Name ?? textNode.GetType().Name);
                        }

                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                // in case our guessing of file path goes awry
            }

            return false;
        }

        public bool DisplayFile(string sourceFilePath, int lineNumber = 0, int column = 0)
        {
            var text = sourceFileResolver.GetSourceFileText(sourceFilePath);
            if (text == null)
            {
                return false;
            }

            Action preprocess = preprocessedFileManager.GetPreprocessAction(sourceFilePath, text);
            documentWell.DisplaySource(sourceFilePath, text.Text, lineNumber, column, preprocess);
            return true;
        }

        public bool DisplayText(string text, string caption = null)
        {
            documentWell.DisplaySource(caption ?? "Text", text, displayPath: false);
            return true;
        }

        private bool DisplayTask(string sourceFilePath, TreeNode parent, string name)
        {
            Target target = parent as Target;
            if (target == null)
            {
                return DisplayFile(sourceFilePath);
            }

            return DisplayTarget(sourceFilePath, target.Name);
        }

        public bool DisplayTarget(string sourceFilePath, string targetName)
        {
            var text = sourceFileResolver.GetSourceFileText(sourceFilePath);
            if (text == null)
            {
                return false;
            }

            var xml = text.XmlRoot;
            int startPosition = 0;
            int line = 0;
            foreach (var element in xml.Elements.First().Elements)
            {
                if (element.Name == "Target" && element.Attributes != null)
                {
                    var nameAttribute = element.AsSyntaxElement.Attributes.FirstOrDefault(a => a.Name == "Name" && a.Value == targetName);
                    if (nameAttribute != null)
                    {
                        startPosition = nameAttribute.ValueNode.Start;
                        break;
                    }
                }
            }

            if (startPosition > 0)
            {
                line = text.GetLineNumberFromPosition(startPosition);
            }

            return DisplayFile(sourceFilePath, line + 1);
        }

        private static ParentedNode GetNode(RoutedEventArgs args)
        {
            var treeViewItem = args.Source as TreeViewItem;
            var node = treeViewItem?.DataContext as ParentedNode;
            return node;
        }

        private IEnumerable BuildResultTree(object resultsObject)
        {
            var results = resultsObject as IEnumerable<SearchResult>;
            if (results == null)
            {
                return results;
            }

            var root = new Folder();

            // root.Children.Add(new Message { Text = "Elapsed " + Elapsed.ToString() });

            foreach (var result in results)
            {
                TreeNode parent = root;

                var parentedNode = result.Node as ParentedNode;
                if (parentedNode != null)
                {
                    var chain = parentedNode.GetParentChain();
                    var project = parentedNode.GetNearestParent<Project>();
                    if (project != null)
                    {
                        var projectProxy = root.GetOrCreateNodeWithName<ProxyNode>(project.Name);
                        projectProxy.Original = project;
                        if (projectProxy.Highlights.Count == 0)
                        {
                            projectProxy.Highlights.Add(project.Name);
                        }

                        parent = projectProxy;
                        parent.IsExpanded = true;
                    }
                }

                var proxy = new ProxyNode();
                proxy.Original = result.Node;
                proxy.Populate(result);
                parent.Children.Add(proxy);
            }

            if (!root.HasChildren)
            {
                root.Children.Add(new Message { Text = "No results found." });
            }

            return root.Children;
        }

        private void TreeViewItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            var treeViewItem = (TreeViewItem)sender;
            var treeView = (TreeView)typeof(TreeViewItem).GetProperty("ParentTreeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(treeViewItem);

            if (PresentationSource.FromDependencyObject(treeViewItem) == null)
            {
                // the item might have disconnected by the time we run this
                return;
            }

            Point topLeftInTreeViewCoordinates = treeViewItem.TransformToAncestor(treeView).Transform(new Point(0, 0));
            var treeViewItemTop = topLeftInTreeViewCoordinates.Y;
            if (treeViewItemTop < 0
                || treeViewItemTop + treeViewItem.ActualHeight > scrollViewer.ViewportHeight
                || treeViewItem.ActualHeight > scrollViewer.ViewportHeight)
            {
                // if the item is not visible or too "tall", don't do anything; let them scroll it into view
                return;
            }

            // if the item is already fully within the viewport vertically, disallow horizontal scrolling
            e.Handled = true;
        }

        private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
        {
            SelectedTreeViewItem = e.OriginalSource as TreeViewItem;
        }
    }
}
