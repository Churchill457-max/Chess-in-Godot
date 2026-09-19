using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// ==========================================================================
// Board
// The "View + Controller" half of the project's Model/View split. Owns one
// BoardModel (the pure rules/state) and is responsible for everything that
// touches Godot directly: drawing squares/pieces/highlights, handling mouse
// input, running the AI's turn, and reading/writing save files. BoardModel
// itself never imports anything Godot-specific — all of that lives here.
//
// CompleteMove() is the single funnel every kind of move (a human's click,
// a promotion choice, or an AI-chosen move) passes through, which is what
// keeps captures/notation/check-detection/game-over/autosave logic from
// needing to be duplicated in three places.
// ==========================================================================
public partial class Board : Node2D
{
	const int TileSize = 80;
	private BoardModel boardModel = new BoardModel();
	private Dictionary<(PieceType, PieceColor), Texture2D> pieceTextures = new();
	private List<Sprite2D> pieceSprites = new();

	// Currently-selected square (null = nothing selected yet).
	private int? selectedRow = null;
	private int? selectedCol = null;

	// Promotion is asynchronous from the player's point of view (they have
	// to pick a piece from a popup), so the move that triggered it has to
	// be held here until FinishPromotion actually completes it.
	private bool awaitingPromotion = false;
	private int pendingFromRow, pendingFromCol, pendingToRow, pendingToCol;
	private Control promotionPicker;

	private bool gameOver = false; 

	// Events Main.cs subscribes to, so this class never needs to know
	// anything about labels/panels/UI — it just announces what happened.
	public event Action<PieceColor> TurnChanged;
	public event Action<string, PieceColor?> GameOver;
	public event Action<PieceType, PieceColor, PieceColor> PieceCaptured;
	public event Action<bool> CheckStatusChanged;
	public event Action<string> MoveRecorded;
	public event Action GameStateRefreshed;

	// Snapshot stack powering Undo — a full BoardModel clone is pushed onto
	// this before every move, so undoing is just popping the last one back
	// off and making it the live model again.
	private List<BoardModel> undoStack = new();

	public PieceColor CurrentTurn => boardModel.CurrentTurn;
	public bool IsCurrentPlayerInCheck() => boardModel.IsKingInCheck(boardModel.CurrentTurn);
	public List<Piece> GetCapturedByWhite() => boardModel.CapturedByWhite;
	public List<Piece> GetCapturedByBlack() => boardModel.CapturedByBlack;
	public List<string> GetMoveHistory() => boardModel.MoveHistory;

	// ---- AI state ----
	private ChessAI chessAI = new ChessAI();
	private bool aiEnabled = false;
	private PieceColor aiColor = PieceColor.Black;
	private AiDifficulty aiDifficulty = AiDifficulty.Advanced;

	// Gates all mouse input until a level has actually been picked from the
	// level-select screen (or a save has been loaded) — without this, a
	// stray click on a hidden board before a game starts could try to
	// operate on state that isn't set up yet.
	private bool gameStarted = false;

	// ---- Save/load ----
	private static readonly string SavePath = "user://savegame.json";
	private static readonly JsonSerializerOptions SaveOptions = new JsonSerializerOptions
	{
		// System.Text.Json only serializes public PROPERTIES by default.
		// Every save-related class here uses public FIELDS instead
		// (matching the rest of the codebase's style), so without this,
		// AutoSave() would silently write out an empty JSON object every
		// time — no error, no crash, just permanently blank save files.
		IncludeFields = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private ColorRect[,] squareNodes = new ColorRect[8, 8];

	// Bumped any time the board state changes OUTSIDE the normal "AI's
	// timer fires and plays its move" flow — i.e. whenever Undo, Restart,
	// or Load swap boardModel out from under a pending AI-move timer. Each
	// scheduled AI-move timer captures the current value when it's set up;
	// if the value has since changed by the time the timer actually fires,
	// that means the board moved on without it, and the stale callback
	// should do nothing rather than act on an outdated assumption. This is
	// what fixes the "AI sometimes skips its move" bug — a timer firing
	// against a board state it was never actually scheduled for.
	private int moveGeneration = 0;

	static readonly Color HighlightColor = new Color(0.6f, 0.8f, 1f, 0.8f);

	public override void _Ready()
	{
		// Build the visual 8x8 grid of alternating light/dark squares.
		// These are separate ColorRect nodes (not just a drawn texture) so
		// individual squares can be recolored for selection/highlight
		// without redrawing anything else.
		for (int row = 0; row < 8; row++)
		{
			for (int col = 0; col < 8; col++)
			{
				var square = new ColorRect();
				square.Size = new Vector2(TileSize, TileSize);
				square.Position = new Vector2(col * TileSize, row * TileSize);
				square.Color = (row + col) % 2 == 0
					? new Color(0.93f, 0.93f, 0.82f)  // light square
					: new Color(0.45f, 0.29f, 0.18f); // dark square
				square.MouseFilter = Control.MouseFilterEnum.Ignore;
				AddChild(square);
				
				squareNodes[row, col] = square;
			}
		}
		boardModel.SetupStartingPosition();
		LoadPieceTextures();
		DrawPieces();
	}
	
	// Serializes the current game (board + AI state) out to disk. Called
	// automatically at the end of every completed move, so the save file
	// is always at most one move stale.
	private void AutoSave()
{
	var data = boardModel.ToSaveData();
	data.AiEnabled = aiEnabled;
	data.AiDifficulty = aiDifficulty;
	string json = JsonSerializer.Serialize(data, SaveOptions);
	using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
	if (file == null)
	{
		// FileAccess.Open returns null on failure rather than throwing —
		// a Godot-specific quirk worth remembering; this checks explicitly
		// rather than assuming the open always succeeds.
		GD.PrintErr($"Autosave failed: {FileAccess.GetOpenError()}");
		return;
	}
	file.StoreString(json);
}

// Restores a previously-saved game from disk, or does nothing if no save
// exists yet / it's unreadable / it's from an incompatible format — all of
// which fail SAFE rather than crashing, since a player clicking Load with
// no prior save is an entirely normal, expected case.
public void LoadGame()
{
	if (!FileAccess.FileExists(SavePath)) return; // nothing saved yet — no-op, not an error

	using var file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
	if (file == null) return;
	string json = file.GetAsText();

	BoardSaveData data;
	try
	{
		data = JsonSerializer.Deserialize<BoardSaveData>(json, SaveOptions);
	}
	catch
	{
		GD.PrintErr("Save file was unreadable — ignoring it.");
		return;
	}
	if (data == null || data.Square == null)
	{
		GD.PrintErr("Save file was empty or incompatible — ignoring it.");
		return;
	}

	boardModel = BoardModel.FromSaveData(data);
	moveGeneration++; // invalidate any AI-move timer that was already pending
	aiEnabled = data.AiEnabled;
	aiDifficulty = data.AiDifficulty;
	gameStarted = true;
	undoStack.Clear();

	// Clear out all transient visual/interaction state left over from
	// whatever was happening before Load was pressed — selection
	// highlights, in-progress move dots, an open promotion picker, etc.
	foreach (var sprite in pieceSprites) sprite.QueueFree();
	pieceSprites.Clear();
	foreach (var dot in legalMoveDots) dot.QueueFree();
	legalMoveDots.Clear();
	foreach (var (r, c) in captureHighlights)
	{
		bool wasLight = (r + c) % 2 == 0;
		squareNodes[r, c].Color = wasLight ? new Color(0.93f, 0.93f, 0.82f) : new Color(0.45f, 0.29f, 0.18f);
	}
	captureHighlights.Clear();
	if (promotionPicker != null)
	{
		promotionPicker.QueueFree();
		promotionPicker = null;
	}
	if (selectedRow.HasValue)
	{
		bool wasLight = (selectedRow.Value + selectedCol.Value) % 2 == 0;
		squareNodes[selectedRow.Value, selectedCol.Value].Color = wasLight
			? new Color(0.93f, 0.93f, 0.82f) : new Color(0.45f, 0.29f, 0.18f);
	}
	selectedRow = null;
	selectedCol = null;
	awaitingPromotion = false;

	// Game-over status is recomputed fresh from the loaded position rather
	// than trusted from the save file, so a stale/hand-edited save can't
	// claim a finished game is still active (or vice versa).
	string status = boardModel.CheckGameEndStatus(boardModel.CurrentTurn);
	gameOver = status != "Active";

	DrawPieces();
	GameStateRefreshed?.Invoke();

	if (gameOver)
	{
		PieceColor? winner = status == "Checkmate"
			? (boardModel.CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White)
			: (PieceColor?)null;
		GameOver?.Invoke(status, winner);
	}
	else if (aiEnabled && boardModel.CurrentTurn == aiColor)
	{
		// If it's still the AI's turn after loading, resume play
		// automatically rather than leaving the game waiting on a human
		// click that will never come.
		int gen = moveGeneration;
		GetTree().CreateTimer(0.5f).Timeout += () => TriggerAIMove(gen);
	}
}
	
	

	// Called from the level-select screen once the player picks an
	// opponent. Sets up AI state, resets to a fresh starting position, and
	// kicks off the AI's first move immediately if it happens to be
	// playing White (aiColor).
	public void StartNewGame(bool enableAi, AiDifficulty difficulty)
	{
		gameStarted = true;
		aiEnabled = enableAi;
		aiDifficulty = difficulty;
		ResetGame(); // fresh board + fires GameStateRefreshed, same as the Restart button
		if (aiEnabled && boardModel.CurrentTurn == aiColor)
		{
			int gen = moveGeneration;
			GetTree().CreateTimer(0.5f).Timeout += () => TriggerAIMove(gen);
		}
	}
	

	// Fired by a scheduled timer roughly half a second after it becomes
	// the AI's turn. Two guard checks at the top before doing any real
	// work: the generation check catches a STALE timer (board changed via
	// Undo/Restart/Load since this was scheduled), and the second line
	// re-confirms it's genuinely still the AI's turn to move right now —
	// together these are what stop the AI from ever acting on outdated
	// assumptions about the game state.
	private void TriggerAIMove(int expectedGeneration)
	{
		if (expectedGeneration != moveGeneration) return; // board changed since this was scheduled — stale, ignore
		if (!aiEnabled || gameOver || boardModel.CurrentTurn != aiColor) return; // not actually AI's turn anymore
		int depth = chessAI.DepthFor(aiDifficulty);
		var move = chessAI.ChooseBestMove(boardModel, aiColor, depth);
		if (move.FromRow == -1) return; // safety guard — shouldn't happen unless the game already ended
		CompleteMove(move.FromRow, move.FromCol, move.ToRow, move.ToCol, move.Promotion);
		DrawPieces();
	}

	private void LoadPieceTextures()
	{
		pieceTextures [(PieceType.Pawn, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/Pawn White.png"); 
		pieceTextures [(PieceType.Knight, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/Knight White.png");
		pieceTextures [(PieceType.Bishop, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/Bishop White.png");
		pieceTextures [(PieceType.Rook, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/Rook White.png");
		pieceTextures [(PieceType.King, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/King White.png");
		pieceTextures [(PieceType.Queen, PieceColor.White)] = GD.Load<Texture2D>("res://Assets/Queen White.png");
		
		pieceTextures [(PieceType.Pawn, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/Pawn Black.png");
		pieceTextures [(PieceType.Knight, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/Knight Black.png");
		pieceTextures [(PieceType.Bishop, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/Bishop Black.png");
		pieceTextures [(PieceType.Rook, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/Rook Black.png");
		pieceTextures [(PieceType.King, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/King Black.png");
		pieceTextures [(PieceType.Queen, PieceColor.Black)] = GD.Load<Texture2D>("res://Assets/Queen Black.png");
	}
	
	// Converts (row, col) into algebraic notation, e.g. (0,0) -> "a8".
	private string SquareName(int row, int col)
	{
		char file = (char)('a' + col);
		int rank = 8 - row;
		return $"{file}{rank}";
	}

	private char PieceLetter(PieceType type)
	{
		switch (type)
		{
			case PieceType.King: return 'K';
			case PieceType.Queen: return 'Q';
			case PieceType.Rook: return 'R';
			case PieceType.Bishop: return 'B';
			case PieceType.Knight: return 'N';
			default: return '\0'; // Pawn gets no letter, standard chess notation
		}
	}
	
	public Texture2D GetPieceTexture(PieceType type, PieceColor color)
	{
		return pieceTextures[(type, color)];
	}
	
	// Clears every existing piece sprite and redraws the board from
	// scratch based on boardModel.Square. Simple and a bit wasteful
	// (destroys/recreates sprites rather than moving existing ones) but
	// keeps rendering logic trivial and always in sync with model state.
	private void DrawPieces()
	{
		foreach (var oldSprite in pieceSprites)
		{
			oldSprite.QueueFree();
		}
		pieceSprites.Clear();
		for (int row = 0; row < 8; row++)
		{
			for (int col = 0; col < 8; col++)
			{
				Piece piece = boardModel.Square[row, col];
				if (piece.Type != PieceType.None)
				{
					var texture = pieceTextures[(piece.Type, piece.Color)];
					
					var sprite = new Sprite2D();
					sprite.Texture = texture;
					
					float scaleFactor = (TileSize * 0.95f) / texture.GetWidth();
					sprite.Scale = new Vector2(scaleFactor, scaleFactor);
					sprite.Position = new Vector2( col * TileSize + TileSize / 2f, row * TileSize + TileSize / 2f);
					AddChild(sprite); 
					pieceSprites.Add(sprite);
				}
			}
		}
	}
	
	// Transient per-selection visuals: green dots for empty legal squares,
	// a red tint for legal capture squares.
	private List<MoveDot> legalMoveDots = new();
	private List<(int, int)> captureHighlights = new();
	static readonly Color LegalMoveColor = new Color(0.4f, 0.9f, 0.4f, 0.6f);
	static readonly Color CaptureHighlightColor = new Color(0.9f, 0.3f, 0.3f, 0.6f);
	
	// Spawns the four promotion-choice piece buttons just outside the
	// board, positioned so the 4-piece stack stays within the board's
	// vertical bounds regardless of which rank the pawn promoted on.
	private void ShowPromotionPicker(PieceColor color)
	{
		promotionPicker = new Control();
		float pickerX = 8 * TileSize + 20; // just outside the board, to the right
		float pickerY = pendingToRow == 7 ? 4 * TileSize : 0; // keep the 4-piece stack within the board's height
		promotionPicker.Position = new Vector2(pickerX, pickerY);
		AddChild(promotionPicker);
		PieceType[] choices = { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };
		for (int i = 0; i < choices.Length; i++)
		{
			PieceType choice = choices[i];
			var button = new TextureButton();
			button.TextureNormal = pieceTextures[(choice, color)];
			button.IgnoreTextureSize = true;
			button.StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered;
			button.Size = new Vector2(TileSize, TileSize);
			button.Position = new Vector2(0, i * TileSize);
			button.Pressed += () => FinishPromotion(choice);
			promotionPicker.AddChild(button);
		}
	}

	// Called once the player picks a promotion piece — completes the move
	// that was held pending since the pawn first reached the far rank.
	private void FinishPromotion(PieceType chosenType)
	{
		CompleteMove(pendingFromRow, pendingFromCol, pendingToRow, pendingToCol, chosenType);
		promotionPicker.QueueFree();
		promotionPicker = null;
		awaitingPromotion = false;
		DrawPieces();
	}
	
	// The single funnel every move (human click, promotion completion, or
	// AI-chosen move) passes through. Handles: pushing an undo snapshot,
	// applying the move to the model, detecting/announcing captures,
	// building algebraic notation, switching turns, detecting
	// check/checkmate/stalemate, scheduling the AI's next move if
	// applicable, and autosaving — all as one atomic sequence so none of
	// this logic has to be duplicated per move source.
	private void CompleteMove(int fromRow, int fromCol, int toRow, int toCol,PieceType promotionType = PieceType.Queen)
	{
		moveGeneration++; // a real move is happening — invalidate any stale pending AI timer
		undoStack.Add(boardModel.Clone());
		Piece movingPiece = boardModel.Square[fromRow, fromCol];
		bool isCastle = movingPiece.Type == PieceType.King && Math.Abs(toCol - fromCol) == 2;
		bool isPromotion = movingPiece.Type == PieceType.Pawn && (toRow == 0 || toRow == 7);
		int whiteCapturesBefore = boardModel.CapturedByWhite.Count;
		int blackCapturesBefore = boardModel.CapturedByBlack.Count;
		boardModel.MovePiece(fromRow, fromCol, toRow, toCol, promotionType);
		 bool wasCapture = boardModel.CapturedByWhite.Count > whiteCapturesBefore || boardModel.CapturedByBlack.Count > blackCapturesBefore;
		if (boardModel.CapturedByWhite.Count > whiteCapturesBefore)
		{
			Piece captured = boardModel.CapturedByWhite[^1];
			PieceCaptured?.Invoke(captured.Type, captured.Color, PieceColor.White);
		}
		else if (boardModel.CapturedByBlack.Count > blackCapturesBefore)
		{
			Piece captured = boardModel.CapturedByBlack[^1];
			PieceCaptured?.Invoke(captured.Type, captured.Color, PieceColor.Black);
		}
		// Build standard algebraic notation for the move history display.
		string notation;
		 if (isCastle)
		{
			notation = toCol == 6 ? "O-O" : "O-O-O";
		}
		 else if (movingPiece.Type == PieceType.Pawn && wasCapture)
		{
			notation = $"{(char)('a' + fromCol)}x{SquareName(toRow, toCol)}{(isPromotion ? "=" + PieceLetter(promotionType) : "")}";
		}
		else 
		{
			notation = $"{PieceLetter(movingPiece.Type)}{(wasCapture ? "x" : "")}{SquareName(toRow, toCol)}{(isPromotion ? "=" + PieceLetter(promotionType) : "")}";
		}
		boardModel.MoveHistory.Add(notation);
		 MoveRecorded?.Invoke(notation);
		boardModel.SwitchTurn();
		TurnChanged?.Invoke(boardModel.CurrentTurn);
		bool inCheck = boardModel.IsKingInCheck(boardModel.CurrentTurn);  
		CheckStatusChanged?.Invoke(inCheck); 
		string status = boardModel.CheckGameEndStatus(boardModel.CurrentTurn);
		if (status == "Checkmate")
		{
			 PieceColor winner = boardModel.CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
			gameOver = true;
			GameOver?.Invoke(status, winner);
		}
		 else if (status == "Stalemate")
		{
			gameOver = true;
			GameOver?.Invoke(status, null);
		}
		 if (aiEnabled && !gameOver && boardModel.CurrentTurn == aiColor)   // new
		{
			// Schedule the AI's reply. Captures the CURRENT generation
			// number at scheduling time — TriggerAIMove compares against
			// this later to detect whether the board has since moved on
			// without it (see moveGeneration's declaration above).
			int gen = moveGeneration;
			GetTree().CreateTimer(0.5f).Timeout += () => TriggerAIMove(gen);
		}
		AutoSave(); 
	}
	
	// Pops the last snapshot off the undo stack and makes it the live
	// board again, clearing any in-progress selection/highlight state in
	// the process.
	public void UndoLastMove()
{
	if (awaitingPromotion) return;
	if (undoStack.Count == 0) return;

	boardModel = undoStack[^1];
	undoStack.RemoveAt(undoStack.Count - 1);
	moveGeneration++; // invalidates any AI-move timer that was scheduled against the move just undone

	if (selectedRow.HasValue)
	{
		bool wasLight = (selectedRow.Value + selectedCol.Value) % 2 == 0;
		squareNodes[selectedRow.Value, selectedCol.Value].Color = wasLight
			? new Color(0.93f, 0.93f, 0.82f) : new Color(0.45f, 0.29f, 0.18f);
	}
	selectedRow = null;
	selectedCol = null;

	foreach (var dot in legalMoveDots) dot.QueueFree();
	legalMoveDots.Clear();

	foreach (var (r, c) in captureHighlights)
	{
		bool wasLight = (r + c) % 2 == 0;
		squareNodes[r, c].Color = wasLight ? new Color(0.93f, 0.93f, 0.82f) : new Color(0.45f, 0.29f, 0.18f);
	}
	captureHighlights.Clear();

	gameOver = false;
	DrawPieces();
	GameStateRefreshed?.Invoke();
}
	
	// Handles all board clicking: first click selects a piece and shows
	// its legal moves, second click either completes a move (or opens the
	// promotion picker if applicable) or just re-selects/deselects.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!gameStarted) return;
		if (aiEnabled && boardModel.CurrentTurn == aiColor) return; 
		if (gameOver) return;
		if(awaitingPromotion) return;
		if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed && mouseEvent.ButtonIndex == MouseButton.Left)
		{
			Vector2 localPos = GetLocalMousePosition();
			int col = (int)(localPos.X / TileSize);
			int row = (int)(localPos.Y / TileSize);
			
			if (row < 0 || row > 7 || col < 0 || col > 7)
			return;
			
			if (selectedRow == null)
			{
				// First click: select a piece belonging to the current
				// player and show its legal destinations.
				Piece piece = boardModel.Square[row, col];
				if (piece.Type != PieceType.None && piece.Color == boardModel.CurrentTurn)
				{
					selectedRow = row;
					selectedCol = col;
					squareNodes[row, col].Color = HighlightColor;
					
					var legalMoves = boardModel.GetTrulyLegalMoves(row, col);
					foreach(var(moveRow, moveCol) in legalMoves)
					{
						Piece targetPiece = boardModel.Square[moveRow, moveCol];
						if (targetPiece.Type != PieceType.None)
						{
							squareNodes[moveRow, moveCol].Color = CaptureHighlightColor;
							captureHighlights.Add((moveRow, moveCol));
						}
						else 
						{
							var dot = new MoveDot();
							dot.Position = new Vector2(
								moveCol * TileSize + TileSize / 2f,
								moveRow * TileSize + TileSize / 2f
							);
							AddChild(dot);
							legalMoveDots.Add(dot);
						}
					}
				}
			}
			else 
			{
				// Second click: attempt to move to the clicked square.
				bool wasLight = (selectedRow.Value + selectedCol.Value) % 2 == 0;
				squareNodes[selectedRow.Value,selectedCol.Value].Color = wasLight 
				? new Color(0.93f, 0.93f, 0.82f)
				: new Color(0.45f, 0.29f, 0.18f);
				
				var legalMoves = boardModel.GetTrulyLegalMoves(selectedRow.Value, selectedCol.Value);
				if(legalMoves.Contains((row, col)))
				{
					Piece movingPiece = boardModel.Square[selectedRow.Value, selectedCol.Value];
					bool isPromotion = movingPiece.Type == PieceType.Pawn && (row == 0 || row == 7);
					if (isPromotion)
					{
						// Defer completing the move until the player picks
						// a promotion piece.
						pendingFromRow = selectedRow.Value;
						pendingFromCol = selectedCol.Value;
						pendingToRow = row;
						pendingToCol = col;
						awaitingPromotion = true;
						ShowPromotionPicker(movingPiece.Color);
					}
					else
					{
						CompleteMove(selectedRow.Value, selectedCol.Value, row, col);
					}
				}
				
				// Whether the move succeeded or the click missed a legal
				// square, clear all selection visuals either way.
				foreach (var dot in legalMoveDots)
				{
					dot.QueueFree();
				}
				legalMoveDots.Clear();
				
				foreach (var(r, c) in captureHighlights)
				{
					bool wasLightHL = (r + c) % 2 == 0;
					squareNodes[r, c].Color = wasLightHL
					? new Color(0.93f, 0.93f, 0.82f)
					: new Color(0.45f, 0.29f, 0.18f);
				}
				captureHighlights.Clear();
				
				selectedRow = null;
				selectedCol = null; 
				
				DrawPieces();
			}
		}
	}

	// Wipes the board back to a fresh starting position and clears every
	// piece of transient state (selection, highlights, undo stack, open
	// promotion picker). Used directly by the Restart button, and also
	// internally by StartNewGame when beginning a brand new game from the
	// level-select screen.
	public void ResetGame()
	{
		boardModel = new BoardModel();
		boardModel.SetupStartingPosition();
		moveGeneration++; // invalidates any AI-move timer scheduled against the previous game
		undoStack.Clear();
		foreach (var sprite in pieceSprites) sprite.QueueFree();
		pieceSprites.Clear();
		foreach (var dot in legalMoveDots) dot.QueueFree();
		legalMoveDots.Clear();
		foreach (var (r, c) in captureHighlights)
		{
			bool wasLight = (r + c) % 2 == 0;
			squareNodes[r, c].Color = wasLight ? new Color(0.93f, 0.93f, 0.82f) : new Color(0.45f, 0.29f, 0.18f);
		}
		captureHighlights.Clear();
		if (promotionPicker != null)
		{
			promotionPicker.QueueFree();
			promotionPicker = null;
		}
		selectedRow = null;
		selectedCol = null;
		gameOver = false;
		awaitingPromotion = false;
		DrawPieces();
		GameStateRefreshed?.Invoke();
	}
}
