using Godot;
using System;
using System.Collections.Generic;

// ==========================================================================
// PieceType / PieceColor / Piece
// Basic chess vocabulary shared by every other file in the project.
// ==========================================================================

public enum PieceType
{
	None,
	King,
	Queen,
	Knight,
	Bishop,
	Rook,
	Pawn
}
public enum PieceColor
{
	None,
	White,
	Black
}

// A single square's contents. Struct (value type) rather than class, so
// copying a Piece around (e.g. into arrays, save data) never risks two
// squares accidentally sharing a reference to "the same" piece.
public struct Piece
{
	public PieceType Type;
	public PieceColor Color;
}

// ==========================================================================
// BoardModel
// Pure chess rules and state. Deliberately has NO Godot rendering/input
// code in it at all (no Node, no _Ready, no drawing) — this is the "Model"
// half of the Model/View split described in the project journal. Board.cs
// is the "View" that owns one of these and turns it into pixels + clicks.
// Keeping this class Godot-agnostic is what let ChessAI.cs simulate
// thousands of hypothetical positions per move without touching the scene
// tree at all.
// ==========================================================================
public class BoardModel
{
	public PieceColor CurrentTurn = PieceColor.White; 
	public Piece [,] Square = new Piece [8 , 8];
	
	public void SwitchTurn()
	{
		CurrentTurn = CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
	}
	
	// Lays out the standard starting position. Row 0/7 = back ranks,
	// row 1/6 = pawns. Godot's row 0 is visually the top of the board.
	public void SetupStartingPosition()
	{
		PieceType[] backRank = {PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
		PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook};
		for (int col=0; col < 8; col++)
		{
			Square [1, col] = new Piece {Type = PieceType.Pawn, Color = PieceColor.Black};
			Square [6, col] = new Piece {Type = PieceType.Pawn, Color = PieceColor.White};
			Square [0, col] = new Piece {Type = backRank[col], Color = PieceColor.Black};
			Square [7, col] = new Piece {Type = backRank[col], Color = PieceColor.White};
		}
	}
	
	
	public List<Piece> CapturedByWhite = new();
	public List<Piece> CapturedByBlack = new();
	public List<string> MoveHistory = new();
	
	// Castling-rights tracking. Once a king or rook has moved (or been
	// captured, since a captured rook obviously can't castle either),
	// that side permanently loses the relevant castling option — these
	// six bools are the entire memory of that history.
	public bool WhiteKingMoved = false;
	public bool BlackKingMoved = false; 
	
	public bool WhiteRookKingsideMoved = false;
	public bool WhiteRookQueensideMoved = false;
	public bool BlackRookKingsideMoved = false;
	public bool BlackRookQueensideMoved = false;
	
	// Set the move immediately after a pawn double-steps; cleared on every
	// other move. Only ever valid for exactly one reply, matching the real
	// chess rule that en passant must be played the very next move or not
	// at all.
	public (int Row, int Col)? EnPassantTarget = null;
	
	// The single place every move actually gets applied to the board array.
	// Handles capture bookkeeping, en passant, castling's rook hop,
	// promotion, castling-rights flag updates, and re-arming/clearing the
	// en passant target — all as one atomic operation.
	public void MovePiece(int fromRow, int fromCol, int toRow, int toCol, PieceType promotionType = PieceType.Queen)
{
	Piece piece = Square[fromRow, fromCol];
	Piece targetPiece = Square[toRow, toCol];

	// En passant is detected, not stored on the move itself: a pawn moving
	// diagonally onto an EMPTY square only makes sense if it's capturing
	// the pawn that just double-stepped past it.
	bool isEnPassant = piece.Type == PieceType.Pawn && fromCol != toCol && targetPiece.Type == PieceType.None;

	if (isEnPassant)
	{
		// The captured pawn isn't on the destination square — it's on the
		// same row it started from, one rank behind where the capturing
		// pawn lands.
		int capturedPawnRow = (piece.Color == PieceColor.White) ? toRow + 1 : toRow - 1;
		Piece capturePawn = Square[capturedPawnRow, toCol];

		if (piece.Color == PieceColor.White)
		{
			CapturedByWhite.Add(capturePawn);
		}
		else
		{
			CapturedByBlack.Add(capturePawn);
		}
		Square[capturedPawnRow, toCol] = new Piece();
	}
	else if (targetPiece.Type != PieceType.None)
	{
		// Ordinary capture — whatever was on the destination square.
		if (piece.Color == PieceColor.White)
		{
			CapturedByWhite.Add(targetPiece);
		}
		else if (piece.Color == PieceColor.Black)
		{
			CapturedByBlack.Add(targetPiece);
		}
	}

	Square[toRow, toCol] = piece;
	Square[fromRow, fromCol] = new Piece();

	// Promotion: a pawn that reaches the far rank is replaced in place by
	// whichever piece type was requested (Queen by default).
	if (piece.Type == PieceType.Pawn && (toRow == 0 || toRow == 7))
	{
		Square[toRow, toCol] = new Piece { Type = promotionType, Color = piece.Color };
	}

	// Castling: a king move of exactly two columns is castling by
	// definition (no other legal king move travels that far). The rook
	// has to be hopped over manually here since MovePiece only ever
	// receives the king's own from/to coordinates.
	if (piece.Type == PieceType.King && Math.Abs(toCol - fromCol) == 2)
	{
		if (toCol == 6)
		{
			Square[toRow, 5] = Square[toRow, 7];
			Square[toRow, 7] = new Piece();
		}
		else if (toCol == 2)
		{
			Square[toRow, 3] = Square[toRow, 0];
			Square[toRow, 0] = new Piece();
		}
	}

	// Update castling-rights flags for the piece that just moved.
	if (piece.Type == PieceType.King)
	{
		if (piece.Color == PieceColor.White)
		{
			WhiteKingMoved = true;
		}
		else
		{
			BlackKingMoved = true;
		}
	}

	if (piece.Type == PieceType.Rook)
	{
		if (piece.Color == PieceColor.White)
		{
			if (fromCol == 0) WhiteRookQueensideMoved = true;
			if (fromCol == 7) WhiteRookKingsideMoved = true;
		}
		else
		{
			if (fromCol == 0) BlackRookQueensideMoved = true;
			if (fromCol == 7) BlackRookKingsideMoved = true;
		}
	}

	// En passant target is only ever valid for the single move right after
	// a pawn double-steps, so it's unconditionally cleared here first, then
	// re-armed only if THIS move was itself a double-step.
	EnPassantTarget = null;
	if (piece.Type == PieceType.Pawn && Math.Abs(toRow - fromRow) == 2)
	{
		int skippedRow = (fromRow + toRow) / 2;
		EnPassantTarget = (skippedRow, fromCol);
	}
}
	
	// ---- Per-piece pseudo-legal move generators ----
	// "Pseudo-legal" means these respect how each piece moves and won't
	// land on/through friendly pieces, but do NOT check whether the move
	// would leave the mover's own king in check. That filtering happens
	// one layer up, in GetTrulyLegalMoves.
	
	public List<(int, int)> GetKingMoves(int row, int col)
	{
		Piece piece = Square[row, col];
		var moves = new List <(int, int)>();
		
		(int, int)[]directions = {
			(0, 1), (0, -1), (1, 0), (-1, 0), 
			(1, 1), (1, -1), (-1, 1), (-1, -1)
		};
		
		foreach(var (dr, dc) in directions)
		{
			int targetRow = row + dr;
			int targetCol = col + dc;
			
			if(targetRow >= 0 && targetRow <= 7 && targetCol >= 0 && targetCol <= 7)
			{
				Piece targetPiece = Square[targetRow, targetCol];
				if(targetPiece.Type == PieceType.None || targetPiece.Color != piece.Color) 
				{
					moves.Add((targetRow, targetCol));
				}
			}
		}
		return moves;
	}
	
	// Castling is generated separately from GetKingMoves rather than folded
	// into it, since it has completely different rules (rights flags, empty
	// squares, and — critically — none of the squares the king passes
	// through may be under attack). It's added on to the king's candidate
	// list explicitly in GetTrulyLegalMoves.
	public List<(int, int)> GetCastlingMoves(int row, int col)
	{
		Piece piece = Square[row, col];
	var moves = new List<(int, int)>();

	if (piece.Color == PieceColor.White)
	{
		if (!WhiteKingMoved && !WhiteRookKingsideMoved)
		{
			if (Square[row, 5].Type == PieceType.None && Square[row, 6].Type == PieceType.None)
			{
				if (!IsSquareAttacked(row, 4, PieceColor.Black) &&
					!IsSquareAttacked(row, 5, PieceColor.Black) &&
					!IsSquareAttacked(row, 6, PieceColor.Black))
				{
					moves.Add((row, 6));
				}
			}
		}
		if (!WhiteKingMoved && !WhiteRookQueensideMoved)
		{
			if (Square[row, 1].Type == PieceType.None && Square[row, 2].Type == PieceType.None && Square[row, 3].Type == PieceType.None)
			{
				if (!IsSquareAttacked(row, 4, PieceColor.Black) &&
					!IsSquareAttacked(row, 3, PieceColor.Black) &&
					!IsSquareAttacked(row, 2, PieceColor.Black))
				{
					moves.Add((row, 2));
				}
			}
		}
	}
	else
	{
		if (!BlackKingMoved && !BlackRookKingsideMoved)
		{
			if (Square[row, 5].Type == PieceType.None && Square[row, 6].Type == PieceType.None)
			{
				if (!IsSquareAttacked(row, 4, PieceColor.White) &&
					!IsSquareAttacked(row, 5, PieceColor.White) &&
					!IsSquareAttacked(row, 6, PieceColor.White))
				{
					moves.Add((row, 6));
				}
			}
		}
		if (!BlackKingMoved && !BlackRookQueensideMoved)
		{
			if (Square[row, 1].Type == PieceType.None && Square[row, 2].Type == PieceType.None && Square[row, 3].Type == PieceType.None)
			{
				if (!IsSquareAttacked(row, 4, PieceColor.White) &&
					!IsSquareAttacked(row, 3, PieceColor.White) &&
					!IsSquareAttacked(row, 2, PieceColor.White))
				{
					moves.Add((row, 2));
				}
			}
		}
	}
	return moves;
	}
	
	public List<(int, int)> GetKnightMoves(int row, int col)
	{
		Piece piece = Square[row, col];
		var moves = new List <(int, int)>();
		
		(int, int)[]offsets = {
			(-2, -1), (-2, 1), (-1, -2), (-1, 2), 
			(1,-2), (1, 2), (2, -1), (2, 1)
		};
		
		foreach(var (dr, dc) in offsets)
		{
			int targetRow = row + dr;
			int targetCol = col + dc;
			
			if(targetRow >= 0 && targetRow <= 7 && targetCol >= 0 && targetCol <= 7)
			{
				Piece targetPiece = Square[targetRow, targetCol];
				if(targetPiece.Type == PieceType.None || targetPiece.Color != piece.Color) 
				{
					moves.Add((targetRow, targetCol));
				}
			}
		}
		return moves;
	}
	
	// Shared "walk in a direction until you hit something" logic, reused by
	// both rooks (orthogonal directions) and bishops (diagonal directions)
	// so the sliding logic itself only has to be written once.
	public List<(int, int)> GetSlidingMoves(int row, int col, (int, int)[] directions)
	{
		Piece piece = Square[row, col];
		var moves = new List <(int, int)>();
		
		foreach (var (dr, dc) in directions)
		{
			int step = 1;
			while (true)
			{
				int targetRow = row + dr * step;
				int targetCol = col + dc * step; 
				
				if (targetRow < 0 || targetRow > 7 || targetCol < 0 || targetCol > 7)
				break;
				
				Piece targetPiece = Square[targetRow, targetCol];
				
				if (targetPiece.Type == PieceType.None)
				{
					moves.Add((targetRow, targetCol));
					step++;
				}
				else if (targetPiece.Color != piece.Color)
				{
					// Can capture, but the slide stops here either way.
					moves.Add((targetRow, targetCol));
					break;
				}
				else 
				{
					// Own piece blocks the slide entirely.
					break;
				}
			}
		}
		return moves;
	}
	
	public List<(int, int)> GetRookMoves(int row, int col)
	{
		(int, int)[] directions = {(0, 1), (0, -1), (1, 0), (-1, 0)};
		return GetSlidingMoves(row, col, directions);
	}
	
	public List<(int, int)> GetBishopMoves(int row, int col)
	{
		(int, int)[] directions = {(1, 1), (1, -1), (-1, 1), (-1, -1)};
		return GetSlidingMoves(row, col, directions);
	}
	
	// A queen simply moves like a rook and bishop combined.
	public List<(int, int)> GetQueenMoves(int row, int col)
	{
		var moves = new  List<(int, int)>();
		moves.AddRange(GetRookMoves(row, col));
		moves.AddRange(GetBishopMoves(row, col));
		return moves;
	}
	
	public List<(int, int)> GetPawnMoves(int row, int col)
{
	Piece piece = Square[row, col];
	var moves = new List <(int, int)>();
	
	// White moves "up" the array (decreasing row), Black moves "down"
	// (increasing row), since row 0 is the visual top of the board.
	int direction = piece.Color == PieceColor.White ? -1 : 1;
	int startingRow = piece.Color == PieceColor.White ? 6 : 1;
	
	//forward move  
	int oneStepRow = row + direction;
	if (oneStepRow >= 0 && oneStepRow <= 7 && Square[oneStepRow, col].Type == PieceType.None)
	{
		moves.Add((oneStepRow, col));
		
		// double from the strating rank 
		if (row == startingRow)
		{
			int twoStepRow = row + direction * 2 ;
			if (Square[twoStepRow, col].Type == PieceType.None)
			{
				moves.Add((twoStepRow, col));
			}
		}
	}
	
	// Diagonal captures — either a normal capture, or landing exactly on
	// the currently-armed en passant target square.
	int[] captureCols = {-1, 1};
	foreach (int dc in captureCols)
	{
		int captureRow = row + direction;
		int captureCol = col + dc;
		
		if (captureRow >= 0 && captureRow <= 7 && captureCol >= 0 && captureCol <= 7)
		{
			Piece targetPiece = Square[captureRow, captureCol];
			bool isNormalCapture = targetPiece.Type != PieceType.None && targetPiece.Color != piece.Color;
			bool isEnPassant = EnPassantTarget.HasValue && EnPassantTarget.Value.Row == captureRow && EnPassantTarget.Value.Col == captureCol;

			if (isNormalCapture || isEnPassant)
			{
				moves.Add((captureRow, captureCol));
			}
		}
	}
	return moves;
}
	
	// Dispatches to the right pseudo-legal generator based on piece type.
	// Note: does NOT include castling — that's layered on separately in
	// GetTrulyLegalMoves, since castling needs the full legality check
	// context (can't castle through/into check).
	public List<(int, int)> GetLegalMoves(int row, int col)
	{
		Piece piece = Square[row, col];
		if(piece.Type == PieceType.Knight)
		{
			return GetKnightMoves(row, col);
		}
		else if(piece.Type == PieceType.Rook)
		{
			return GetRookMoves(row, col);
		}
		else if(piece.Type == PieceType.Bishop)
		{
			return GetBishopMoves(row, col);
		}
		else if(piece.Type == PieceType.Queen)
		{
			return GetQueenMoves(row, col);
		}
		else if(piece.Type == PieceType.King)
		{
			return GetKingMoves(row, col);
		}
		else if(piece.Type == PieceType.Pawn)
		{
			return GetPawnMoves(row, col);
		}
		return new List<(int, int)>();
	}
	
	// Brute-force "is any piece of byColor able to move onto this square?"
	// check, used for both check detection and castling's "can't move
	// through check" rule. O(64 squares) per call — fine at this project's
	// scale, but the reason ChessAI's deep search avoids calling this
	// indirectly wherever it can (see the "pseudo-legal" note in ChessAI.cs).
	public bool IsSquareAttacked(int row, int col, PieceColor byColor)
	{
		for (int r = 0; r < 8; r++)
		{
			for (int c = 0; c < 8; c++)
			{
				Piece piece = Square[r, c];
				if(piece.Type != PieceType.None && piece.Color == byColor)
				{
					var attackerMoves = GetLegalMoves (r, c);
					if(attackerMoves.Contains((row, col)))
					{
						return true;
					}
				}
			}
		}
		return false;
	}
	
	public (int, int) FindKing(PieceColor color)
	{
		for (int r = 0; r < 8; r++)
		{
			for( int c = 0; c < 8; c++)
			{
				Piece piece = Square [r, c];
				if(piece.Type == PieceType.King && piece.Color == color)
				{
					return(r, c);
				}
			}
		}
		return (-1, -1);
	}
	
	public bool IsKingInCheck(PieceColor color)
	{
		var (kingRow, kingCol) = FindKing(color);
		PieceColor opponentColor = color == PieceColor.White ? PieceColor.Black : PieceColor.White;
		return IsSquareAttacked(kingRow, kingCol, opponentColor);
	}
	
	// Deep-copies the entire board state, including captures, history, and
	// every rights flag. This is the workhorse behind "what if" simulation:
	// GetTrulyLegalMoves clones before testing each candidate move, and the
	// AI clones before every node of its search tree, specifically so none
	// of that hypothetical exploration ever touches the real game state.
	public BoardModel Clone()
	{
		var newBoard = new BoardModel();
		for (int r = 0; r < 8; r++)
		{
			for (int c = 0; c < 8; c++)
			{
				newBoard.Square[r, c] = Square[r, c];
			}
		}
		newBoard.EnPassantTarget = EnPassantTarget;
		newBoard.WhiteKingMoved = WhiteKingMoved;
		newBoard.BlackKingMoved = BlackKingMoved;
		newBoard.WhiteRookQueensideMoved = WhiteRookQueensideMoved;
		newBoard.WhiteRookKingsideMoved = WhiteRookKingsideMoved;
		newBoard.BlackRookQueensideMoved = BlackRookQueensideMoved;
		newBoard.BlackRookKingsideMoved = BlackRookKingsideMoved;
		newBoard.CapturedByWhite = new List<Piece>(CapturedByWhite);
		newBoard.CapturedByBlack = new List<Piece>(CapturedByBlack);
		newBoard.MoveHistory = new List<string>(MoveHistory);
		return newBoard;
	}
	
	// ---- Save/load conversion ----
	// Converts this live BoardModel into a plain, JSON-friendly
	// BoardSaveData snapshot. Kept deliberately dumb/mechanical (just field
	// copying) — the actual serialization happens in Board.cs, which also
	// sets IncludeFields = true on its JsonSerializerOptions. That option
	// matters a lot: System.Text.Json only serializes public PROPERTIES by
	// default, and every class in this project (BoardModel, Piece,
	// PieceData, BoardSaveData) uses public FIELDS instead, matching the
	// rest of the codebase's style. Without IncludeFields = true, this
	// entire save file silently serializes to an empty object.
	public BoardSaveData ToSaveData()
{
	var data = new BoardSaveData();
	data.Square = new PieceData[8][];
	for (int r = 0; r < 8; r++)
	{
		data.Square[r] = new PieceData[8];
		for (int c = 0; c < 8; c++)
		{
			data.Square[r][c] = new PieceData { Type = Square[r, c].Type, Color = Square[r, c].Color };
		}
	}
	data.CurrentTurn = CurrentTurn;
	data.CapturedByWhite = CapturedByWhite.ConvertAll(p => new PieceData { Type = p.Type, Color = p.Color });
	data.CapturedByBlack = CapturedByBlack.ConvertAll(p => new PieceData { Type = p.Type, Color = p.Color });
	data.MoveHistory = new List<string>(MoveHistory);
	data.WhiteKingMoved = WhiteKingMoved;
	data.BlackKingMoved = BlackKingMoved;
	data.WhiteRookKingsideMoved = WhiteRookKingsideMoved;
	data.WhiteRookQueensideMoved = WhiteRookQueensideMoved;
	data.BlackRookKingsideMoved = BlackRookKingsideMoved;
	data.BlackRookQueensideMoved = BlackRookQueensideMoved;
	// (int, int)? doesn't serialize cleanly as a named tuple, so it's split
	// into two independent nullable ints here and reassembled below.
	data.EnPassantRow = EnPassantTarget?.Row;
	data.EnPassantCol = EnPassantTarget?.Col;
	return data;
}

	// The reverse of ToSaveData — rebuilds a fully live BoardModel from a
	// deserialized snapshot. Static because there's no existing instance to
	// populate; it constructs a brand new one.
	public static BoardModel FromSaveData(BoardSaveData data)
{
	var board = new BoardModel();
	for (int r = 0; r < 8; r++)
	{
		for (int c = 0; c < 8; c++)
		{
			board.Square[r, c] = new Piece { Type = data.Square[r][c].Type, Color = data.Square[r][c].Color };
		}
	}
	board.CurrentTurn = data.CurrentTurn;
	board.CapturedByWhite = data.CapturedByWhite.ConvertAll(p => new Piece { Type = p.Type, Color = p.Color });
	board.CapturedByBlack = data.CapturedByBlack.ConvertAll(p => new Piece { Type = p.Type, Color = p.Color });
	board.MoveHistory = new List<string>(data.MoveHistory);
	board.WhiteKingMoved = data.WhiteKingMoved;
	board.BlackKingMoved = data.BlackKingMoved;
	board.WhiteRookKingsideMoved = data.WhiteRookKingsideMoved;
	board.WhiteRookQueensideMoved = data.WhiteRookQueensideMoved;
	board.BlackRookKingsideMoved = data.BlackRookKingsideMoved;
	board.BlackRookQueensideMoved = data.BlackRookQueensideMoved;
	board.EnPassantTarget = data.EnPassantRow.HasValue
		? (data.EnPassantRow.Value, data.EnPassantCol.Value)
		: ((int, int)?)null;
	return board;
}
	
	// The fully-filtered legal move list: takes the pseudo-legal candidates
	// (plus castling, for kings), simulates each one on a throwaway clone,
	// and only keeps the ones that DON'T leave the mover's own king in
	// check. This is the expensive-but-correct version used anywhere a
	// move might actually be played (player clicks, the AI's chosen move) —
	// contrast with ChessAI's internal search, which uses the cheaper
	// pseudo-legal GetLegalMoves several plies deep as a performance
	// tradeoff.
	public List <(int, int)> GetTrulyLegalMoves (int row, int col)
	{
		Piece piece = Square[row, col];
		var candidateMoves = GetLegalMoves(row, col);
		
		if (piece.Type == PieceType.King)
		{
			candidateMoves.AddRange(GetCastlingMoves(row, col));
		}
		
		var legalMoves = new List<(int, int)>();
		
		foreach (var (targetRow, targetCol) in candidateMoves)
		{
			var boardCopy = Clone();
			boardCopy.MovePiece(row, col, targetRow, targetCol);
			
			if(!boardCopy.IsKingInCheck(piece.Color))
			{
				legalMoves.Add((targetRow, targetCol));
			}
		}
		return legalMoves;
	}
	
	// "Active" / "Checkmate" / "Stalemate" for the side about to move. If
	// that color has zero truly-legal moves anywhere on the board, the game
	// is over — checkmate if the king is currently attacked, stalemate
	// otherwise (a draw).
	public string CheckGameEndStatus(PieceColor color)
	{
		bool hasAnyLegalMove = false;
		
		for(int r = 0; r < 8; r++)
		{
			for(int c = 0; c < 8; c++)
			{
				Piece piece = Square[r, c];
				
				if(piece.Type != PieceType.None && piece.Color == color)
				{
					if (GetTrulyLegalMoves(r, c).Count > 0)
					{
						hasAnyLegalMove = true;
					}
					
				}
			}
		}
		if (hasAnyLegalMove) return "Active";
		return IsKingInCheck(color) ? "Checkmate" : "Stalemate";
	}
}
