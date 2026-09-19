using Godot;
using System;
using System.Collections.Generic;

// Difficulty levels the player can pick from the level-select screen.
// Purely a lookup key — see DifficultyDepths below for what each one
// actually means mechanically (how many plies deep the AI searches).
public enum AiDifficulty
{
	Beginner,
	Intermediate,
	Advanced,
	Master,
	GrandMaster
}

// ==========================================================================
// ChessAI
// The opponent's "brain." Implements negamax search with alpha-beta
// pruning over a plain material-count evaluation. Deliberately stateless —
// every public/private method here takes the board it's operating on as a
// parameter rather than storing one on the class, so a single ChessAI
// instance can safely be reused turn after turn with no leftover state to
// reset.
// ==========================================================================
public class ChessAI
{
	// Arbitrarily large scores used as stand-ins for "someone just won" and
	// as a starting "worse than anything real" bound before any actual
	// move has been evaluated yet.
	private const int MateScore = 1_000_000;
	private const int InitialBound = 2_000_000;
	

	
	// Difficulty -> search depth (in plies/half-moves) lookup. This is the
	// ONLY thing that changes between difficulty levels — the algorithm
	// itself never changes, just how many moves ahead it's allowed to look
	// before it has to commit to an answer.
	private static readonly Dictionary<AiDifficulty, int> DifficultyDepths = new()
	{
		{ AiDifficulty.Beginner, 1 },
		{ AiDifficulty.Intermediate, 2 },
		{ AiDifficulty.Advanced, 3 },
		{ AiDifficulty.Master, 4 },
		{ AiDifficulty.GrandMaster, 5 }
	};

	public int DepthFor(AiDifficulty difficulty) => DifficultyDepths[difficulty];

	// Standard chess piece values in "pawns" — the units the evaluation
	// function's score is expressed in. King is intentionally absent here;
	// it's handled separately in Evaluate() as a win/loss condition rather
	// than a point value.
	private static readonly Dictionary<PieceType, int> PieceValues = new()
	{
		{ PieceType.Pawn, 1 },
		{ PieceType.Knight, 3 },
		{ PieceType.Bishop, 3 },
		{ PieceType.Rook, 5 },
		{ PieceType.Queen, 9 }
	};

	// The entry point — called once per AI turn from Board.cs. Tries every
	// truly-legal move for `color`, imagines the opponent's best response
	// to each (via Minimax), and returns whichever move leads to the best
	// outcome for the AI once that imagined exchange plays out.
	//
	// Deliberately uses GetTrulyLegalMoves (the fully rules-checked list)
	// rather than the cheaper pseudo-legal list Minimax uses internally —
	// this is the one place a move actually gets played, so it has to be
	// 100% legal, no shortcuts.
	public (int FromRow, int FromCol, int ToRow, int ToCol, PieceType Promotion) ChooseBestMove(BoardModel board, PieceColor color, int depth)
	{
		int bestScore = -InitialBound;
		(int FromRow, int FromCol, int ToRow, int ToCol, PieceType Promotion) bestMove = (-1, -1, -1, -1, PieceType.Queen);

		for (int row = 0; row < 8; row++)
		{
			for (int col = 0; col < 8; col++)
			{
				Piece piece = board.Square[row, col];
				if (piece.Type == PieceType.None || piece.Color != color) continue;

				var legalMoves = board.GetTrulyLegalMoves(row, col);
				foreach (var (toRow, toCol) in legalMoves)
				{
					// Simulate the move on a throwaway copy — the real
					// board is never touched during "thinking."
					var boardCopy = board.Clone();
					boardCopy.MovePiece(row, col, toRow, toCol);

					// Negamax's core trick: recursing into the opponent's
					// perspective and negating the result converts "their
					// best score" into "how good/bad this is for me,"
					// without needing separate maximize/minimize code
					// paths for each side.
					int score = -Minimax(boardCopy, Opponent(color), depth - 1, -InitialBound, InitialBound);

					if (score > bestScore)
					{
						bestScore = score;
						bestMove = (row, col, toRow, toCol, PieceType.Queen);
					}
				}
			}
		}
		return bestMove;
	}

	// The recursive search itself. Alternates perspective every call via
	// the Opponent(color) + sign-flip pattern, going one ply shallower each
	// time, until `depth` hits zero and it falls back to a static
	// evaluation of whatever position it landed on.
	//
	// alpha = the best score the current side can already guarantee in
	//         this branch.
	// beta  = the best score the OPPONENT already found in a sibling
	//         branch one level up.
	// If alpha ever reaches or exceeds beta, the opponent would never
	// actually allow the game to reach this branch (they already have a
	// better alternative elsewhere), so the rest of it is skipped —
	// that's the entire alpha-beta pruning optimization, in one line.
	private int Minimax(BoardModel board, PieceColor color, int depth, int alpha, int beta)
	{
		if (depth == 0)
		{
			return Evaluate(board, color);
		}

		int best = -InitialBound;
		bool hasMove = false;

		for (int row = 0; row < 8; row++)
		{
			for (int col = 0; col < 8; col++)
			{
				Piece piece = board.Square[row, col];
				if (piece.Type == PieceType.None || piece.Color != color) continue;

				// NOTE — pseudo-legal, not truly-legal: GetTrulyLegalMoves
				// clones the board an EXTRA time per candidate just to
				// check king safety. Doing that at every ply of a
				// multi-move-deep search would multiply an already
				// expensive full-board Clone() exponentially with depth.
				// Using the cheaper unfiltered list here is a deliberate
				// performance tradeoff: the AI's ACTUAL move (chosen in
				// ChooseBestMove, above) is always fully legal — only this
				// deeper lookahead is a slightly approximate simulation.
				// Evaluate()'s missing-king check below is the safety net
				// for the rare case this approximation matters.
				var moves = board.GetLegalMoves(row, col); // pseudo-legal — see design note
				foreach (var (toRow, toCol) in moves)
				{
					hasMove = true;
					var boardCopy = board.Clone();
					boardCopy.MovePiece(row, col, toRow, toCol);

					int score = -Minimax(boardCopy, Opponent(color), depth - 1, -beta, -alpha);

					if (score > best) best = score;
					if (best > alpha) alpha = best;
					if (alpha >= beta) return best; // alpha-beta cutoff
				}
			}
		}

		// No legal moves for this color at this point in the imagined
		// line = checkmate or stalemate from the search's point of view.
		// Just score the position as-is rather than recursing into nothing.
		if (!hasMove)
		{
			return Evaluate(board, color);
		}

		return best;
	}

	// Pure material-count evaluation: sum up piece values, own pieces
	// positive, opponent's negative. Nothing here accounts for board
	// control, king safety, piece activity, or pawn structure — which is
	// exactly why Beginner (depth 1) hangs pieces so readily: at one ply,
	// a move is judged only by the board immediately after it, with zero
	// visibility into whether the opponent can just capture back next turn.
	private int Evaluate(BoardModel board, PieceColor color)
	{
		int score = 0;
		bool foundOwnKing = false;
		bool foundEnemyKing = false;

		for (int row = 0; row < 8; row++)
		{
			for (int col = 0; col < 8; col++)
			{
				Piece piece = board.Square[row, col];
				if (piece.Type == PieceType.None) continue;

				if (piece.Type == PieceType.King)
				{
					// Kings aren't point-valued — their presence/absence
					// is treated as a win/loss condition instead. This
					// also doubles as a safety net against the pseudo-legal
					// search above ever having allowed a "king capture."
					if (piece.Color == color) foundOwnKing = true;
					else foundEnemyKing = true;
					continue;
				}

				int value = PieceValues[piece.Type];
				score += piece.Color == color ? value : -value;
			}
		}

		if (!foundOwnKing) return -MateScore;
		if (!foundEnemyKing) return MateScore;

		return score;
	}

	// Small helper: flips White<->Black. Used every time the recursion
	// needs to say "now think from the other player's seat."
	private PieceColor Opponent(PieceColor color)
	{
		return color == PieceColor.White ? PieceColor.Black : PieceColor.White;
	}
}
