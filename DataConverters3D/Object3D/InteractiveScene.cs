﻿/*
Copyright (c) 2018, John Lewin, Lars Brubaker
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D.UndoCommands;
using MatterHackers.PolygonMesh;
using MatterHackers.RayTracer;
using MatterHackers.Localizations;
using MatterHackers.VectorMath;
using Newtonsoft.Json;

namespace MatterHackers.DataConverters3D
{
	public class InteractiveScene : IObject3D
	{
		public event EventHandler SelectionChanged;
		public event EventHandler<InvalidateArgs> Invalidated;

		private IObject3D _selectedItem;

		public InteractiveScene()
		{
		}

		[JsonIgnore]
		public IObject3D SelectedItemRoot { get; private set; }

		[JsonIgnore]
		public IObject3D SelectedItem
		{
			get => _selectedItem;
			set
			{
				if (_selectedItem != value)
				{
					if (_selectedItem is SelectionGroupObject3D)
					{
						// If the selected item is a SelectionGroup, collapse its contents into the root
						// of the scene when it loses focus
						Children.Modify(list =>
						{
							_selectedItem.CollapseInto(list);
						});
					}

					_selectedItem = value;

					// When setting SelectedItem, SelecteItemRoot is either the SelectedItem if rooted, or the first ancestor in the root of the scene
					if (_selectedItem?.Parent.Parent != null)
					{
						this.SelectedItemRoot = _selectedItem.Ancestors().FirstOrDefault(o => o.Parent.Parent == null);
					}
					else
					{
						this.SelectedItemRoot = _selectedItem;
					}

					SelectionChanged?.Invoke(this, null);
				}
			}
		}

		[JsonIgnore]
		public UndoBuffer UndoBuffer { get; } = new UndoBuffer() { MaxUndos = 10 };

		[JsonIgnore]
		public bool ShowSelectionShadow { get; set; } = true;

		public void Save(Stream stream, Action<double, string> progress = null)
		{
			// Serialize the scene to disk using a modified Json.net pipeline with custom ContractResolvers and JsonConverters
			try
			{
				this.PersistAssets(progress);

				// Clear the selection before saving
				List<IObject3D> selectedItems = new List<IObject3D>();

				if (this.SelectedItem != null)
				{
					if (this.SelectedItem is SelectionGroupObject3D selectionGroup)
					{
						foreach (var item in selectionGroup.Children)
						{
							selectedItems.Add(item);
						}
					}
					else
					{
						selectedItems.Add(this.SelectedItem);
					}
				}

				var streamWriter = new StreamWriter(stream);
				streamWriter.Write(this.ToJson());
				streamWriter.Flush();

				// Restore the selection after saving
				foreach (var item in selectedItems)
				{
					this.AddToSelection(item);
				}
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Error saving file: ", ex.Message);
			}
		}

		public void Save(string mcxPath, Action<double, string> progress = null)
		{
			using (var stream = new FileStream(mcxPath, FileMode.Create, FileAccess.Write))
			{
				this.Save(stream, progress);
			}
		}

		[JsonIgnore]
		// TODO: Remove from InteractiveScene - coordinate debug details between MeshViewer and Inspector directly
		public IObject3D DebugItem { get; set; }

		public void SelectLastChild()
		{
			if (Children.Count > 0)
			{
				SelectedItem = Children.Last();
			}
		}

		public void SelectFirstChild()
		{
			if (Children.Count > 0)
			{
				SelectedItem = Children.First();
			}
		}

		public void ClearSelection()
		{
			SelectedItem = null;
		}

		public void SetSelection(IEnumerable<IObject3D> items)
		{
			if (items.Count() == 1)
			{
				// Add a single item directly as the SelectedItem
				SelectedItem = items.First();
			}
			else
			{
				// Add a range of items wrapped with a new SelectionGroup
				var SelectionGroup = new SelectionGroupObject3D(items)
				{
					Name = "Selection".Localize()
				};

				// Move selected items into a new SelectionGroup
				this.Children.Modify(list =>
				{
					foreach (var item in items)
					{
						list.Remove(item);
					}

					// Add the SeletionGroup as the first child
					list.Insert(0, SelectionGroup);
				});

				SelectedItem = SelectionGroup;
			}
		}

		public void AddToSelection(IObject3D itemToAdd)
		{
			var selectedItem = SelectedItem;
			if (itemToAdd == selectedItem || selectedItem?.Children?.Contains(itemToAdd) == true)
			{
				return;
			}

			if (selectedItem != null)
			{
				if (selectedItem is SelectionGroupObject3D)
				{
					// Remove from the scene root
					this.Children.Modify(list => list.Remove(itemToAdd));

					// Move into the SelectionGroup
					selectedItem.Children.Modify(list => list.Add(itemToAdd));
				}
				else // add a new selection group and add to its children
				{
					// We're adding a new item to the selection. To do so we wrap the selected item
					// in a new group and with the new item. The selection will continue to grow in this
					// way until it's applied, due to a loss of focus or until a group operation occurs
					var newSelectionGroup = new SelectionGroupObject3D(new[] { selectedItem, itemToAdd })
					{
						Name = "Selection".Localize()
					};

					this.Children.Modify(list =>
					{
						list.Remove(itemToAdd);
						list.Remove(selectedItem);
						// add the seletionGroup as the first item so we can hit it first
						list.Insert(0, newSelectionGroup);
					});

					SelectedItem = newSelectionGroup;
				}
			}
			else if (Children.Contains(itemToAdd))
			{
				SelectedItem = itemToAdd;
			}
			else
			{
				throw new Exception("Unable to select external object. Item must be in the scene to be selected.");
			}
		}

		public void Load(IObject3D sourceItem)
		{
			if (sourceItem != null)
			{
				sourceItem.Invalidated -= SourceItem_Invalidated;
			}

			this.sourceItem = sourceItem;
			sourceItem.Invalidated += SourceItem_Invalidated;
		}

		private void SourceItem_Invalidated(object sender, InvalidateArgs e)
		{
			this.Invalidated?.Invoke(this, e);
		}

		#region IObject3D

		private IObject3D sourceItem = new Object3D();

		public string OwnerID { get => sourceItem.OwnerID; set => sourceItem.OwnerID = value; }
		public SafeList<IObject3D> Children { get => sourceItem.Children; set => sourceItem.Children = value; }
		public IObject3D Parent { get => sourceItem.Parent; set => sourceItem.Parent = value; }
		public Color Color { get => sourceItem.Color; set => sourceItem.Color = value; }
		public int MaterialIndex { get => sourceItem.MaterialIndex; set => sourceItem.MaterialIndex = value; }
		public PrintOutputTypes OutputType { get => sourceItem.OutputType; set => sourceItem.OutputType = value; }
		public Matrix4X4 Matrix { get => sourceItem.Matrix; set => sourceItem.Matrix = value; }
		public string TypeName => sourceItem.TypeName;
		public Mesh Mesh { get => sourceItem.Mesh; set => sourceItem.Mesh = value; }
		public string MeshPath { get => sourceItem.MeshPath; set => sourceItem.MeshPath = value; }
		public string Name { get => sourceItem.Name; set => sourceItem.Name = value; }
		public bool Persistable => sourceItem.Persistable;
		public bool Visible { get => sourceItem.Visible; set => sourceItem.Visible = value; }
		public string ID { get => sourceItem.ID; set => sourceItem.ID = value; }

		public bool CanEdit => false;

		public bool CanApply => false;

		public bool CanRemove => false;

		public bool DrawSelection { get; set; } = true;

		[JsonIgnore]
		public bool RebuildLocked => false;

		public IObject3D Clone() => sourceItem.Clone();

		public string ToJson() => sourceItem.ToJson();

		public long GetLongHashCode() => sourceItem.GetLongHashCode();

		public IPrimitive TraceData() => sourceItem.TraceData();

		public void SetMeshDirect(Mesh mesh) => sourceItem.SetMeshDirect(mesh);

		public void Invalidate(InvalidateArgs invalidateType)
		{
			this.Invalidated?.Invoke(this, invalidateType);
		}

		public AxisAlignedBoundingBox GetAxisAlignedBoundingBox(Matrix4X4 matrix)
		{
			return sourceItem.GetAxisAlignedBoundingBox(matrix);
		}

		public void Apply(UndoBuffer undoBuffer)
		{
			throw new NotImplementedException();
		}

		public void Remove(UndoBuffer undoBuffer)
		{
			throw new NotImplementedException();
		}

		public void Rebuild(UndoBuffer undoBuffer)
		{
			throw new NotImplementedException();
		}

		public RebuildLock RebuildLock()
		{
			throw new NotImplementedException();
		}

		#endregion

	}
}
