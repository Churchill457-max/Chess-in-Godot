using Godot;
using System;
using System.Collections.Generic;

// ==========================================================================
// SaveData
// Plain, JSON-friendly "data transfer" shapes used only for persistence —
// these never appear on screen and have no game logic of their own. They
// exist so BoardModel.ToSaveData()/FromSaveData() has something simple and
// stable to convert to/from, independent of however the live game classes
// (BoardModel, Piece) might change shape in the future.
//
// Both classes use public FIELDS rather than properties, matching the rest
// of the codebase's style — which matters a lot here specifically, since
// System.Text.Json only serializes public PROPERTIES by default. Board.cs's
// JsonSerializerOptions sets IncludeFields = true to compensate; without
// that flag, every save file this produces would silently serialize to an
// empty JSON object with no error raised anywhere.
// ==========================================================================

// Mirrors the live Piece struct, just as a plain serializable class instead
// of a struct — kept as a deliberate separate type (rather than reusing
// Piece directly) so the save FORMAT doesn't silently break if Piece's own
// shape ever changes for rendering/gameplay reasons.
public class PieceData
{
	public PieceType Type;
	public PieceColor Color;
}

// The full snapshot of one saved game: board position, whose turn it is,
// both captured-piece lists, move history in notation, all six castling
// rights flags, the en passant target (split into two nullable ints, since
// a named value tuple doesn't serialize cleanly), and — added later — the
// AI's enabled/difficulty state, so resuming a save also resumes the right
// opponent behavior rather than defaulting back to "AI off."
public class BoardSaveData
{
	public PieceData[][] Square;
	public PieceColor CurrentTurn;
	public List<PieceData> CapturedByWhite;
	public List<PieceData> CapturedByBlack;
	public List<string> MoveHistory;
	public bool WhiteKingMoved;
	public bool BlackKingMoved;
	public bool WhiteRookKingsideMoved;
	public bool WhiteRookQueensideMoved;
	public bool BlackRookKingsideMoved;
	public bool BlackRookQueensideMoved;
	public int? EnPassantRow;
	public int? EnPassantCol;
	public bool AiEnabled;
	public AiDifficulty AiDifficulty;
}
