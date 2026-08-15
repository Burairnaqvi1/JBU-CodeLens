{
  Serial number validation for JBU CodeLens.

  Included by JBU.CodeLens.iss, and by the test harness that exercises it. It
  lives in its own file so the harness runs this exact code rather than a
  second copy of it that could drift.

  A key looks like  JBUC-4K7P2-9WX3M-QT58R  and carries its own proof: the last
  character is a check character worked out from the fourteen before it. Setup
  therefore holds no list of valid keys, there is nothing in this script to
  read off and reuse, and keys can be issued without rebuilding the installer.

  The alphabet leaves out 0, O, 1, I and L, so a key read off a printed sheet
  cannot be mistyped through characters that look alike.

  This stops casual copying and nothing more. Any check that runs entirely on
  the machine being installed to can be bypassed by someone determined enough
  to try; that is true of every offline scheme, and pretending otherwise would
  be the mistake.
}
const
  SerialAlphabet = '23456789ABCDEFGHJKMNPQRSTUVWXYZ';
  SerialPrefix = 'JBUC';
  SerialLength = 22;

{ Position of C in the alphabet, or -1 when it is not a legal character. }
function AlphabetIndex(C: Char): Integer;
begin
  Result := Pos(C, SerialAlphabet) - 1;
end;

function CheckSerial(Serial: String): Boolean;
var
  Entered, Body: String;
  I, Index, Weighted: Integer;
begin
  Result := False;

  { Typed by hand from a sheet of paper, so case and stray spaces are forgiven. }
  Entered := Uppercase(Trim(Serial));

  if Length(Entered) <> SerialLength then Exit;
  if Copy(Entered, 1, 4) <> SerialPrefix then Exit;
  if (Entered[5] <> '-') or (Entered[11] <> '-') or (Entered[17] <> '-') then Exit;

  { The three groups joined up: fourteen characters of key, then the check. }
  Body := Copy(Entered, 6, 5) + Copy(Entered, 12, 5) + Copy(Entered, 18, 5);

  Weighted := 0;
  for I := 1 to 14 do
  begin
    Index := AlphabetIndex(Body[I]);
    if Index < 0 then Exit;
    { Weighted by position, so swapping two characters round breaks the sum, 
      a plain total would accept the transposition, which is the commonest
      mistake made copying a key by hand. }
    Weighted := Weighted + (Index * I);
  end;

  Index := AlphabetIndex(Body[15]);
  if Index < 0 then Exit;

  Result := Index = (Weighted mod Length(SerialAlphabet));
end;
