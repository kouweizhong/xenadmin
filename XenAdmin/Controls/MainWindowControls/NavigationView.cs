﻿/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using XenAdmin.Commands;
using XenAdmin.Core;
using XenAdmin.Model;
using XenAdmin.Network;
using XenAdmin.XenSearch;

using XenAPI;

namespace XenAdmin.Controls.MainWindowControls
{
    public partial class NavigationView : UserControl
    {
        #region Private fields

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly MainWindowTreeBuilder treeBuilder;

        private VirtualTreeNode _highlightedDragTarget;
        private int ignoreRefreshTreeView;
        private bool calledRefreshTreeView;

        #endregion

        #region Events

        [Browsable(true)]
        public event Action SearchTextChanged;

        [Browsable(true)]
        public event Action<List<SelectedItem>> TreeViewSelectionChanged;

        [Browsable(true)]
        public event Action TreeNodeBeforeSelected;

        [Browsable(true)]
        public event Action TreeNodeClicked;

        [Browsable(true)]
        public event Action TreeNodeRightClicked;

        [Browsable(true)]
        public event Action TreeViewRefreshed;

        [Browsable(true)]
        public event Action TreeViewRefreshSuspended;

        [Browsable(true)]
        public event Action TreeViewRefreshResumed;

        #endregion

        public NavigationView()
        {
            InitializeComponent();

            treeView.ImageList = Images.ImageList16;
            if (treeView.ItemHeight < 18)
                treeView.ItemHeight = 18;
            //otherwise it's too close together on XP and the icons crash into each other

            VirtualTreeNode n = new VirtualTreeNode(Messages.XENCENTER);
            n.NodeFont = Program.DefaultFont;
            treeView.Nodes.Add(n);
            treeView.SelectedNode = treeView.Nodes[0];

            treeBuilder = new MainWindowTreeBuilder(treeView);
        }

        public TreeSearchBox.Mode NavigationMode { get; set; }

        public Search CurrentSearch { get; set; }

        #region SearchBox

        public string SearchText
        {
            get { return searchTextBox.Text; }
        }


        public void SaveSearchBoxState()
        {
            searchTextBox.SaveState();
        }

        public void RestoreSearchBoxState()
        {
            searchTextBox.RestoreState();
        }

        public void ResetSeachBox()
        {
            searchTextBox.Reset();
        }


        private void searchTextBox_TextChanged(object sender, EventArgs e)
        {
            if (SearchTextChanged != null)
                SearchTextChanged();
        }

        #endregion

        #region TreeView

        public bool TreeViewContainsFocus
        {
            get { return treeView.ContainsFocus; }
        }


        /// <summary>
        /// Selects the specified object in the treeview.
        /// </summary>
        /// <param name="o">The object to be selected.</param>
        /// <param name="expand">A value specifying whether the node should be expanded when it's found. 
        /// If false, the node is left in the state it's found in.</param>
        /// <returns>A value indicating whether selection was successful.</returns>
        public bool SelectObject(IXenObject o, bool expand)
        {
            bool cancelled = false;
            if (treeView.Nodes.Count == 0)
                return false;

            bool success = treeView.SelectObject(o, treeView.Nodes[0], expand, ref cancelled);

            if (!success && !cancelled && SearchText.Length > 0)
            {
                // if the node could not be found and the node *is* selectable then it means that
                // node isn't visible with the current search. So clear the search and try and
                // select the object again.

                // clear search.
                ResetSeachBox();

                // update the treeview
                RefreshTreeView();

                // and try again.
                return treeView.SelectObject(o, treeView.Nodes[0], expand, ref cancelled);
            }
            return success;
        }

        public bool TryToSelectNewNode(Predicate<object> tagMatch, bool selectNode, bool expandNode, bool ensureNodeVisible)
        {
            return treeView.TryToSelectNewNode(tagMatch, selectNode, expandNode, ensureNodeVisible);
        }

        public void EditSelectedNode()
        {
            treeView.LabelEdit = true;
            treeView.SelectedNode.BeginEdit();
        }

        public void FocusTreeView()
        {
            treeView.Focus();
        }

        public void SuspendRefreshTreeView()
        {
            Program.AssertOnEventThread();

            if (ignoreRefreshTreeView == 0)
                calledRefreshTreeView = false;
            ignoreRefreshTreeView++;

            if (TreeViewRefreshSuspended != null)
                TreeViewRefreshSuspended();
        }

        public void ResumeRefreshTreeView()
        {
            Program.AssertOnEventThread();

            ignoreRefreshTreeView--;
            if (ignoreRefreshTreeView == 0 && calledRefreshTreeView)
                Program.MainWindow.RequestRefreshTreeView();

            if (TreeViewRefreshResumed != null)
                TreeViewRefreshResumed();
        }

        /// <summary>
        /// Refreshes the main tree view. A new node tree is built on the calling thread and then merged into the main tree view
        /// on the main program thread.
        /// </summary>
        public void RefreshTreeView()
        {
            var newRootNode = treeBuilder.CreateNewRootNode(CurrentSearch.AddFullTextFilter(SearchText), NavigationMode);

            Program.Invoke(this, () =>
            {
                if (ignoreRefreshTreeView > 0)
                {
                    calledRefreshTreeView = true;
                    return;
                }

                ignoreRefreshTreeView++;  // Some events can be ignored while rebuilding the tree

                try
                {
                    treeBuilder.RefreshTreeView(newRootNode, SearchText, NavigationMode);
                }
                catch (Exception exn)
                {
                    log.Error(exn, exn);
#if DEBUG
                    if (Debugger.IsAttached)
                        throw;
#endif
                }
                finally
                {
                    ignoreRefreshTreeView--;
                }
                
                if (TreeViewRefreshed != null)
                TreeViewRefreshed();
            });
        }


        private static Pool PoolAncestorOfNode(VirtualTreeNode node)
        {
            while (node != null)
            {
                var pool = node.Tag as Pool;
                if (pool != null)
                    return pool;

                node = node.Parent;
            }
            return null;
        }

        private static Host HostAncestorOfNode(VirtualTreeNode node)
        {
            while (node != null)
            {
                var host = node.Tag as Host;
                if (host != null)
                    return host;

                node = node.Parent;
            }
            return null;
        }

        private bool CanDrag()
        {
            if ((NavigationMode != XenAdmin.Controls.TreeSearchBox.Mode.Objects
                && NavigationMode != XenAdmin.Controls.TreeSearchBox.Mode.Organization))
            {
                return Program.MainWindow.SelectionManager.Selection.AllItemsAre<Host>() || Program.MainWindow.SelectionManager.Selection.AllItemsAre<VM>(vm => !vm.is_a_template);
            }
            foreach (SelectedItem item in Program.MainWindow.SelectionManager.Selection)
            {
                if (item.XenObject == null || item.Connection == null || !item.Connection.IsConnected)
                    return false;
            }
            return true;
        }

        private List<DragDropCommand> GetDragDropCommands(VirtualTreeNode targetNode, IDataObject dragData)
        {
            List<DragDropCommand> commands = new List<DragDropCommand>();
            commands.Add(new DragDropAddHostToPoolCommand(Program.MainWindow.CommandInterface, targetNode, dragData));
            commands.Add(new DragDropMigrateVMCommand(Program.MainWindow.CommandInterface, targetNode, dragData));
            commands.Add(new DragDropRemoveHostFromPoolCommand(Program.MainWindow.CommandInterface, targetNode, dragData));

            if (NavigationMode == TreeSearchBox.Mode.Organization)
            {
                commands.Add(new DragDropTagCommand(Program.MainWindow.CommandInterface, targetNode, dragData));
                commands.Add(new DragDropIntoFolderCommand(Program.MainWindow.CommandInterface, targetNode, dragData));
            }

            return commands;
        }

        private void ClearHighlightedDragTarget()
        {
            if (_highlightedDragTarget != null)
            {
                _highlightedDragTarget.BackColor = treeView.BackColor;
                _highlightedDragTarget.ForeColor = treeView.ForeColor;
                _highlightedDragTarget = null;
                treeBuilder.HighlightedDragTarget = null;
            }
        }

        private void HandleNodeRightClick(VirtualTreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            if (treeView.SelectedNodes.Count == 0)
            {
                treeView.SelectedNode = e.Node;

                if (treeView.SelectedNode != e.Node)  // if node is unselectable in TreeView_BeforeSelect: CA-26615
                {
                    return;
                }
            }
            else if (treeView.SelectedNodes.Contains(e.Node))
            {
                // don't change the selection - just show the menu.
            }
            else if (treeView.CanSelectNode(e.Node))
            {
                treeView.SelectedNode = e.Node;
            }
            else
            {
                // can't select node - don't show menu.
                return;
            }

            if (TreeNodeRightClicked != null)
                TreeNodeRightClicked();

            TreeContextMenu.Items.Clear();

            if (e.Node == treeView.Nodes[0] && treeView.SelectedNodes.Count == 1)
            {
                // XenCenter (top most)

                TreeContextMenu.Items.Add(new CommandToolStripMenuItem(new AddHostCommand(Program.MainWindow.CommandInterface), true));
                TreeContextMenu.Items.Add(new CommandToolStripMenuItem(new NewPoolCommand(Program.MainWindow.CommandInterface, new SelectedItem[0]), true));
                TreeContextMenu.Items.Add(new CommandToolStripMenuItem(new ConnectAllHostsCommand(Program.MainWindow.CommandInterface), true));
                TreeContextMenu.Items.Add(new CommandToolStripMenuItem(new DisconnectAllHostsCommand(Program.MainWindow.CommandInterface), true));
            }
            else
            {
                TreeContextMenu.Items.AddRange(Program.MainWindow.ContextMenuBuilder.Build(Program.MainWindow.SelectionManager.Selection));
            }

            int insertIndex = TreeContextMenu.Items.Count;

            if (TreeContextMenu.Items.Count > 0)
            {
                CommandToolStripMenuItem lastItem = TreeContextMenu.Items[TreeContextMenu.Items.Count - 1] as CommandToolStripMenuItem;

                if (lastItem != null && lastItem.Command is PropertiesCommand)
                    insertIndex--;
            }

            AddExpandCollapseItems(insertIndex, treeView.SelectedNodes);
            AddOrgViewItems(insertIndex, treeView.SelectedNodes);

            if (TreeContextMenu.Items.Count > 0)
                TreeContextMenu.Show(treeView, e.Location);
        }

        private void AddExpandCollapseItems(int insertIndex, IList<VirtualTreeNode> nodes)
        {
            if (nodes.Count == 1 && nodes[0].Nodes.Count == 0)
                return;

            Command cmd = new CollapseChildTreeNodesCommand(Program.MainWindow.CommandInterface, nodes);
            if (cmd.CanExecute())
                TreeContextMenu.Items.Insert(insertIndex, new CommandToolStripMenuItem(cmd, true));

            cmd = new ExpandTreeNodesCommand(Program.MainWindow.CommandInterface, nodes);
            if (cmd.CanExecute())
                TreeContextMenu.Items.Insert(insertIndex, new CommandToolStripMenuItem(cmd, true));
        }

        private void AddOrgViewItems(int insertIndex, IList<VirtualTreeNode> nodes)
        {
            if ((NavigationMode != XenAdmin.Controls.TreeSearchBox.Mode.Objects
                 && NavigationMode != XenAdmin.Controls.TreeSearchBox.Mode.Organization)
                || nodes.Count == 0)
            {
                return;
            }

            Command cmd = new RemoveFromFolderCommand(Program.MainWindow.CommandInterface, nodes);

            if (cmd.CanExecute())
                TreeContextMenu.Items.Insert(insertIndex, new CommandToolStripMenuItem(cmd, true));

            cmd = new UntagCommand(Program.MainWindow.CommandInterface, nodes);

            if (cmd.CanExecute())
                TreeContextMenu.Items.Insert(insertIndex, new CommandToolStripMenuItem(cmd, true));
        }


        private void treeView_AfterLabelEdit(object sender, VirtualNodeLabelEditEventArgs e)
        {
            VirtualTreeNode node = e.Node;
            treeView.LabelEdit = false;
            Folder folder = e.Node.Tag as Folder;
            GroupingTag groupingTag = e.Node.Tag as GroupingTag;
            Command command = null;
            object newTag = null;

            EventHandler<RenameCompletedEventArgs> completed = delegate(object s, RenameCompletedEventArgs ee)
            {
                Program.Invoke(this, delegate
                {
                    ResumeRefreshTreeView();

                    if (ee.Success)
                    {
                        // the new tag is updated on the node here. This ensures that the node stays selected 
                        // when the treeview is refreshed. If you don't set the tag like this, the treeview refresh code notices 
                        // that the tags are different and selects the parent node instead of this node.

                        // if the command fails for some reason, the refresh code will correctly revert the tag back to the original.
                        node.Tag = newTag;
                        RefreshTreeView();

                        // since the selected node doesn't actually change, then a selectionsChanged message doesn't get fired
                        // and the selection doesn't get updated to be the new tag/folder. Do it manually here instead.
                        treeView_SelectionsChanged(treeView, EventArgs.Empty);
                    }
                });
            };

            if (!string.IsNullOrEmpty(e.Label))
            {
                if (folder != null)
                {
                    RenameFolderCommand cmd = new RenameFolderCommand(Program.MainWindow.CommandInterface, folder, e.Label);
                    command = cmd;
                    cmd.Completed += completed;
                    newTag = new Folder(null, e.Label);
                }
                else if (groupingTag != null)
                {
                    RenameTagCommand cmd = new RenameTagCommand(Program.MainWindow.CommandInterface, groupingTag.Group.ToString(), e.Label);
                    command = cmd;
                    cmd.Completed += completed;
                    newTag = new GroupingTag(groupingTag.Grouping, groupingTag.Parent, e.Label);
                }
            }

            if (command != null && command.CanExecute())
            {
                command.Execute();
            }
            else
            {
                ResumeRefreshTreeView();
                e.CancelEdit = true;
            }
        }

        private void treeView_BeforeSelect(object sender, VirtualTreeViewCancelEventArgs e)
        {
            if (e.Node == null)
                return;

            if (!treeView.CanSelectNode(e.Node))
            {
                e.Cancel = true;
                return;
            }

            if (TreeNodeBeforeSelected != null)
                TreeNodeBeforeSelected();
        }

        private void treeView_ItemDrag(object sender, VirtualTreeViewItemDragEventArgs e)
        {
            foreach (VirtualTreeNode node in e.Nodes)
            {
                if (node == null || node.TreeView == null)
                {
                    return;
                }
            }

            // select the node if it isn't already selected
            if (e.Nodes.Count == 1 && treeView.SelectedNode != e.Nodes[0])
            {
                treeView.SelectedNode = e.Nodes[0];
            }

            if (CanDrag())
            {
                DoDragDrop(new List<VirtualTreeNode>(e.Nodes).ToArray(), DragDropEffects.Move);
            }
        }

        private void treeView_DragDrop(object sender, DragEventArgs e)
        {
            ClearHighlightedDragTarget();

            VirtualTreeNode targetNode = treeView.GetNodeAt(treeView.PointToClient(new Point(e.X, e.Y)));

            foreach (DragDropCommand cmd in GetDragDropCommands(targetNode, e.Data))
            {
                if (cmd.CanExecute())
                {
                    cmd.Execute();
                    return;
                }
            }
        }

        private void treeView_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            VirtualTreeNode targetNode = treeView.GetNodeAt(treeView.PointToClient(new Point(e.X, e.Y)));
            foreach (DragDropCommand cmd in GetDragDropCommands(targetNode, e.Data))
            {
                if (cmd.CanExecute())
                {
                    e.Effect = DragDropEffects.Move;
                    return;
                }
            }
        }

        private void treeView_DragLeave(object sender, EventArgs e)
        {
            ClearHighlightedDragTarget();
        }

        private void treeView_DragOver(object sender, DragEventArgs e)
        {
            // CA-11457: When dragging in resource tree, view doesn't scroll
            // http://www.fmsinc.com/freE/NewTips/NET/NETtip21.asp

            const int SCROLL_REGION = 20;

            Point pt = treeView.PointToClient(Cursor.Position);
            VirtualTreeNode targetNode = treeView.GetNodeAt(treeView.PointToClient(new Point(e.X, e.Y)));

            if ((pt.Y + SCROLL_REGION) > treeView.Height)
            {
                Win32.SendMessage(treeView.Handle, Win32.WM_VSCROLL, new IntPtr(1), IntPtr.Zero);
            }
            else if (pt.Y < SCROLL_REGION)
            {
                Win32.SendMessage(treeView.Handle, Win32.WM_VSCROLL, IntPtr.Zero, IntPtr.Zero);
            }

            VirtualTreeNode targetToHighlight = null;

            foreach (DragDropCommand cmd in GetDragDropCommands(targetNode, e.Data))
            {
                if (cmd.CanExecute())
                    targetToHighlight = cmd.HighlightNode;
            }

            if (targetToHighlight != null)
            {
                if (_highlightedDragTarget != targetToHighlight)
                {
                    ClearHighlightedDragTarget();
                    treeBuilder.HighlightedDragTarget = targetToHighlight.Tag;
                    _highlightedDragTarget = targetToHighlight;
                    _highlightedDragTarget.BackColor = SystemColors.Highlight;
                    _highlightedDragTarget.ForeColor = SystemColors.HighlightText;
                }
                e.Effect = DragDropEffects.Move;
            }
            else
            {
                ClearHighlightedDragTarget();
                e.Effect = DragDropEffects.None;
            }
        }

        private void treeView_KeyUp(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Apps:
                    if (treeView.SelectedNode != null)
                    {
                        treeView.SelectedNode.EnsureVisible();
                        HandleNodeRightClick(new VirtualTreeNodeMouseClickEventArgs(treeView.SelectedNode,
                            MouseButtons.Right, 1,
                            treeView.SelectedNode.Bounds.X,
                            treeView.SelectedNode.Bounds.Y + treeView.SelectedNode.Bounds.Height));
                    }
                    break;

                case Keys.F2:
                    var cmd = new PropertiesCommand(Program.MainWindow.CommandInterface, Program.MainWindow.SelectionManager.Selection);
                    if (cmd.CanExecute())
                        cmd.Execute();
                    break;
            }
        }

        private void treeView_NodeMouseClick(object sender, VirtualTreeNodeMouseClickEventArgs e)
        {
            try
            {
                if (treeView.Nodes.Count > 0)
                    HandleNodeRightClick(e);

                if (TreeNodeClicked != null)
                    TreeNodeClicked();
            }
            catch (Exception exn)
            {
                log.Error(exn, exn);
                // Swallow this exception -- there's no point throwing it on.
#if DEBUG
                throw;
#endif
            }
        }

        private void treeView_NodeMouseDoubleClick(object sender, VirtualTreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (e.Node == null)
                return;

            IXenConnection conn = e.Node.Tag as IXenConnection;
            if (conn == null)
            {
                var obj = e.Node.Tag as IXenObject;
                if (obj != null)
                    conn = obj.Connection;
            }
            if (conn != null && !conn.IsConnected)
            {
                new ReconnectHostCommand(Program.MainWindow.CommandInterface, conn).Execute();
                return;
            }

            VM vm = e.Node.Tag as VM;
            if (vm != null)
            {
                Command cmd = null;

                if (vm.is_a_template)
                {
                    cmd = new NewVMCommand(Program.MainWindow.CommandInterface, Program.MainWindow.SelectionManager.Selection);
                }
                else if (vm.power_state == vm_power_state.Halted && vm.allowed_operations.Contains(vm_operations.start))
                {
                    cmd = new StartVMCommand(Program.MainWindow.CommandInterface, Program.MainWindow.SelectionManager.Selection);
                }
                else if (vm.power_state == vm_power_state.Suspended && vm.allowed_operations.Contains(vm_operations.resume))
                {
                    cmd = new ResumeVMCommand(Program.MainWindow.CommandInterface, Program.MainWindow.SelectionManager.Selection);
                }

                if (cmd != null && cmd.CanExecute())
                {
                    treeView.SelectedNode = e.Node;
                    cmd.Execute();
                }
                return;
            }

            Host host = e.Node.Tag as Host;
            if (host != null)
            {
                Command cmd = new PowerOnHostCommand(Program.MainWindow.CommandInterface, host);
                if (cmd.CanExecute())
                {
                    treeView.SelectedNode = e.Node;
                    cmd.Execute();
                }
            }
        }

        private void treeView_SelectionsChanged(object sender, EventArgs e)
        {
            // this is fired when the selection of the main treeview changes.

            List<SelectedItem> items = new List<SelectedItem>();
            foreach (VirtualTreeNode node in treeView.SelectedNodes)
            {
                GroupingTag groupingTag = node.Tag as GroupingTag;
                IXenObject xenObject = node.Tag as IXenObject;

                if (xenObject != null)
                {
                    items.Add(new SelectedItem(xenObject, xenObject.Connection, HostAncestorOfNode(node), PoolAncestorOfNode(node)));
                }
                else
                {
                    items.Add(new SelectedItem(groupingTag));
                }
            }

            if (TreeViewSelectionChanged != null)
                TreeViewSelectionChanged(items);
        }

        #endregion
    }
}
