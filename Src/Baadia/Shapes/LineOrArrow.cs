﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using Loyc;
using Loyc.Collections;
using Loyc.Geometry;
using Util.UI;
using Util.WinForms;
using Coord = System.Single;
using PointT = Loyc.Geometry.Point<float>;
using VectorT = Loyc.Geometry.Vector<float>;

namespace BoxDiagrams
{
	public class Arrowhead
	{
		public Arrowhead(MarkerPolygon geometry, float width, float shift = 0, float scale = 6) { Geometry = geometry; Width = width; Shift = shift; Scale = scale; }
		public readonly MarkerPolygon Geometry;
		public readonly float Width, Shift;
		public float Scale;

		protected static PointT P(Coord x, Coord y) { return new PointT(x, y); }
		static readonly MarkerPolygon arrow45deg = new MarkerPolygon {
			Points = new[] { P(-1, 1), P(0,0), P(0,0), P(-1,-1) }.AsListSource(),
			Divisions = new[] { 2 }.AsListSource()
		};
		static readonly MarkerPolygon arrow30deg = new MarkerPolygon {
			Points = new[] { P(-1, 0.5f), P(0,0), P(0,0), P(-1,-0.5f) }.AsListSource(),
			Divisions = new[] { 2 }.AsListSource()
		};
		public static readonly Arrowhead Arrow45deg = new Arrowhead(arrow45deg, 0);
		public static readonly Arrowhead Arrow30deg = new Arrowhead(arrow30deg, 0);
		public static readonly Arrowhead Diamond = new Arrowhead(MarkerPolygon.Diamond, 2, -1, 4);
		public static readonly Arrowhead Circle = new Arrowhead(MarkerPolygon.Circle, 2, -1, 3);
	}

	public class LineOrArrow : Shape
	{
		static int NextZOrder = 0x10000000;

		public LineOrArrow(List<PointT> points = null) {
			Points = points ?? new List<PointT>();
			TextTopLeft.Justify = 0.5f;
			TextBottomRight.Justify = 0.5f;
			_detachedZOrder = NextZOrder++;
		}
		public Arrowhead FromArrow, ToArrow;
		public LinearText TextTopLeft, TextBottomRight;
		public Anchor _fromAnchor, _toAnchor;
		public Anchor FromAnchor
		{
			get { return _fromAnchor; }
			set {
				if (_fromAnchor != null)
					_fromAnchor.Shape.AttachedShapes.Remove(this);
				if ((_fromAnchor = value) != null)
					_fromAnchor.Shape.AttachedShapes.Add(this);
			}
		}
		public Anchor ToAnchor
		{
			get { return _toAnchor; }
			set {
				if (_toAnchor != null)
					_toAnchor.Shape.AttachedShapes.Remove(this);
				if ((_toAnchor = value) != null)
					_toAnchor.Shape.AttachedShapes.Add(this);
			}
		}
		public List<PointT> Points; // includes cached anchor point(s)
		public int _detachedZOrder;

		public override DoOrUndo AttachedShapeChanged(AnchorShape other)
		{
			DoOrUndo fromAct = null, toAct = null;
			if (_fromAnchor != null && _fromAnchor.Shape == other)
				fromAct = AttachedAnchorChanged(_fromAnchor, false);
			if (_toAnchor != null && _toAnchor.Shape == other)
				toAct = AttachedAnchorChanged(_toAnchor, true);
			if (fromAct != null && toAct != null) {
				return @do => {
					fromAct(@do);
					toAct(@do);
				};
			}
			return fromAct ?? toAct;
		}

		static int AngleMod256(VectorT v)
		{
			return (byte)(v.Angle() * (128.0 / Math.PI));
		}

		private DoOrUndo AttachedAnchorChanged(Anchor anchor, bool toSide)
		{
			PointT p0 = default(PointT), p1 = default(PointT);
			List<PointT> old = null;
			return @do =>
			{
				IList<PointT> points = Points;
				if (toSide) points = points.ReverseView();
				
				Debug.Assert(points.Count >= 2);
				if (points.Count < 2)
					return;

				_bbox = null;
				if (@do) {
					// save undo info in either (p0, p1) or (old) for complicated cases
					p0 = points[0];
					p1 = points[1];
					old = new List<PointT>(Points);
					
					var newAnchor = anchor.Point;
					LineSegment<float> one = newAnchor.To(newAnchor.Add(points[1].Sub(points[0]))), two;
					if (points.Count > 2)
						two = points[1].To(points[2]);
					else
						two = one.B.To(one.B.Add(one.B.Sub(one.A).Rot90())); // fake it

					points[0] = newAnchor;
					PointT? itsc = one.ComputeIntersection(two, LineType.Infinite), itsc2;
					if (itsc != null && !(points.Count == 2 && (toSide ? _fromAnchor : _toAnchor) != null))
						points[1] = itsc.Value;
					else
						itsc = points[1];

					if (points.Count >= 3 && points[1] == points[2]) {
						int remove = 1;
						if (points.Count > 3) {
							int a0 = AngleMod256(points[1].Sub(points[0])), a1 = AngleMod256(points[3].Sub(points[2]));
							if ((a0 & 127) == (a1 & 127))
								remove = 2;
						}
						points.RemoveRange(1, remove);
					} else if (points.Count >= 4 && (itsc2 = points[0].To(points[1]).ComputeIntersection(points[2].To(points[3]))) != null) {
						points.RemoveRange(1, 2);
						points.Insert(1, itsc2.Value);
					} else
						old = null; // save memory
				} else {
					if (old != null)
						Points = old;
					else {
						points[0] = p0;
						points[1] = p1;
					}
				}
			};
		}
		
		public override void AddLLShapes(MSet<LLShape> list)
		{
			int z = ZOrder;
			if (Points.Count >= 2) {
				list.Add(new LLPolyline(Style, Points) { ZOrder = z });
				
				// hacky temporary solution
				int half = (Points.Count-1)/2;
				var midVec = Points[half+1].Sub(Points[half]);
				if (TextTopLeft.Text != null) {
					list.Add(new LLTextShape(
						Style, TextTopLeft.Text, LLTextShape.JustifyUpperCenter, Points[half], new VectorT(midVec.Length(), 100))
						{ AngleDeg = (float)midVec.AngleDeg() });
				}
			}
		}

		public override Shape Clone()
		{
			var copy = (LineOrArrow)MemberwiseClone();
			// Points are often changed after cloning... yeah, it's hacky.
			_bbox = null; copy._bbox = null; 
			return copy;
		}
		BoundingBox<float> _bbox;
		public override BoundingBox<float> BBox
		{
			get {
				if (_bbox != null)
					Debug.Assert(_bbox.Contains(Points[0]) && _bbox.Contains(Points[Points.Count - 1]));
				return _bbox = _bbox ?? Points.ToBoundingBox();
			}
		}
		
		public override void AddAdorners(MSet<LLShape> list, SelType selMode, VectorT hitTestRadius)
		{
			if (selMode == SelType.Partial)
			{
				AddAdorner(list, Points[0], hitTestRadius);
				AddAdorner(list, Points[Points.Count - 1], hitTestRadius);
			}
			else if (selMode == SelType.Yes)
				for (int i = 0; i < Points.Count; i++)
					AddAdorner(list, Points[i], hitTestRadius);
		}

		private void AddAdorner(MSet<LLShape> list, PointT point, VectorT hitTestRadius)
		{
			list.Add(new LLMarker(SelAdornerStyle, point, hitTestRadius.X, MarkerPolygon.Square));
		}

		public override int ZOrder
		{
			get {
				int z = _detachedZOrder;
				if (FromAnchor != null)
					z = Math.Min(z, FromAnchor.Shape.ZOrder - 1);
				if (ToAnchor != null)
					z = Math.Min(z, ToAnchor.Shape.ZOrder - 1);
				return z;
			}
		}

		static int AngleMod8(VectorT v)
		{
			return (int)Math.Round(v.Angle() * (4 / Math.PI)) & 7;
		}

		new class HitTestResult : Shape.HitTestResult
		{
			public HitTestResult(Shape shape, Cursor cursor, int pointOrSeg) 
				: base(shape, cursor) { PointOrSegment = pointOrSeg; }
			public readonly int PointOrSegment;
		}

		public override Shape.HitTestResult HitTest(PointT pos, VectorT hitTestRadius, SelType sel)
		{
			if (!BBox.Inflated(hitTestRadius.X, hitTestRadius.Y).Contains(pos))
				return null;
			
			if (sel == SelType.Partial) {
				if (PointsAreNear(pos, Points[0], hitTestRadius))
					return new HitTestResult(this, Cursors.SizeAll, 0);
				if (PointsAreNear(pos, Points[Points.Count-1], hitTestRadius))
					return new HitTestResult(this, Cursors.SizeAll, Points.Count-1);
			} else if (sel == SelType.Yes) {
				for (int i = 0; i < Points.Count; i++)
				{
					if (PointsAreNear(pos, Points[i], hitTestRadius))
						return new HitTestResult(this, Cursors.SizeAll, i);
				}
			}
			
			PointT projected;
			float? where = LLShape.HitTestPolyline(pos, hitTestRadius.X, Points, EmptyList<int>.Value, out projected);
			if (where != null)
			{
				if (sel == SelType.Yes) {
					int seg = (int)where.Value;
					var angle = AngleMod8(Points[seg+1].Sub(Points[seg]));
					switch (angle & 3) {
						case 0: return new HitTestResult(this, Cursors.SizeNS, seg);
						case 1: return new HitTestResult(this, Cursors.SizeNESW, seg);
						case 2: return new HitTestResult(this, Cursors.SizeWE, seg);
						case 3: return new HitTestResult(this, Cursors.SizeNWSE, seg);
					}
				} else {
					return new HitTestResult(this, Cursors.Arrow, -1);
				}
			}
			return null;
		}

		public override void OnKeyPress(KeyPressEventArgs e, UndoStack undoStack)
		{
			e.Handled = true;
			char ch = e.KeyChar;
			if (ch >= ' ') {
				undoStack.Do(@do => {
					if (@do) 
						TextTopLeft.Text += ch;
					else
						TextTopLeft.Text = TextTopLeft.Text.Left(TextTopLeft.Text.Length - 1);
				}, true);
			}
		}
		public override void OnKeyDown(KeyEventArgs e, UndoStack undoStack)
		{
			if (e.Modifiers == 0 && e.KeyCode == Keys.Back && TextTopLeft.Text.Length > 0)
			{
				char last = TextTopLeft.Text[TextTopLeft.Text.Length - 1];
				undoStack.Do(@do => {
					if (@do)
						TextTopLeft.Text = TextTopLeft.Text.Left(TextTopLeft.Text.Length - 1);
					else
						TextTopLeft.Text += last;
				}, true);
			}
		}

		public override void Dispose()
		{
			base.Dispose();
			FromAnchor = null; // detach
			ToAnchor = null; // detach
		}
	}
}