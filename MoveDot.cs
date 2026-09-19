using Godot;
using System;

// Tiny standalone node representing one "you can move here" indicator dot.
// Drawn procedurally via _Draw() rather than as a sprite/texture, since
// it's just a flat-colored circle — one MoveDot instance is spawned per
// empty legal-move square whenever a piece is selected, and freed again
// once a move is made or the selection is cleared.
public partial class MoveDot : Node2D
{
	public override void _Draw()
	{
		DrawCircle(Vector2.Zero, 12f, new Color(0.2f, 0.6f, 0.2f, 0.9f));
	}
}
