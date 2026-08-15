; Tests for the serial number validation.
;
; Installs nothing. It runs CheckSerial over a set of known cases, writes the
; results to a file and aborts. It includes the real SerialCheck.iss, so what is
; under test is the code the installer actually ships rather than a copy of it
; that could drift.
;
; Run with:
;   ISCC.exe packaging\SerialCheck.Tests.iss
;   packaging\JBU-CodeLens-SerialTest.exe /VERYSILENT /out=<results file>
;
; The accepted keys below were issued by New-SerialKey.ps1. Both implementations
; have to agree: if a key this script rejects is one the generator produced, the
; two have drifted apart and one of them is wrong.

[Setup]
AppName=JBU CodeLens serial number tests
AppVersion=1.0
DefaultDirName={tmp}\jbucodelens-serialtest
OutputDir=.
OutputBaseFilename=JBU-CodeLens-SerialTest
Uninstallable=no
CreateAppDir=no
PrivilegesRequired=lowest

[Code]
#include "SerialCheck.iss"

var
  Log: String;
  Failures: Integer;

procedure Expect(Serial: String; Expected: Boolean; Note: String);
var
  Actual: Boolean;
  Verdict, ExpectedText, ActualText: String;
begin
  Actual := CheckSerial(Serial);

  if Expected then ExpectedText := 'accept' else ExpectedText := 'reject';
  if Actual then ActualText := 'accept' else ActualText := 'reject';

  if Actual = Expected then
  begin
    Verdict := 'PASS';
  end
  else
  begin
    Verdict := 'FAIL';
    Failures := Failures + 1;
  end;

  Log := Log + Verdict + '  expected ' + ExpectedText + ', got ' + ActualText +
         '  [' + Serial + ']  ' + Note + #13#10;
end;

function InitializeSetup(): Boolean;
begin
  Log := '';
  Failures := 0;

  { Keys issued by New-SerialKey.ps1, all must be accepted. }
  Expect('JBUC-4NM9C-35SKX-VFG3X', True,  'issued key');
  Expect('JBUC-QZENK-N5GH8-BSEYG', True,  'issued key');
  Expect('JBUC-86ZHS-3K2PA-T26Q5', True,  'issued key');
  Expect('JBUC-YABZ6-2VJX4-5VYAJ', True,  'issued key');
  Expect('JBUC-2TUQ7-JETUQ-TF68G', True,  'issued key');

  { Keys get typed by hand off a sheet of paper, so case and surrounding
    space are forgiven. }
  Expect('jbuc-4nm9c-35skx-vfg3x', True,  'lower case');
  Expect('  JBUC-4NM9C-35SKX-VFG3X  ', True, 'padded with spaces');

  { Everything below must be refused. }
  Expect('JBUC-4NM9C-35SKX-VFG3W', False, 'wrong check character');
  Expect('ABCD-4NM9C-35SKX-VFG3X', False, 'wrong prefix');
  Expect('JBUC-4NM9C-35SKX-VFG3',  False, 'too short');
  Expect('JBUC-4NM9C-35SKX-VFG3XX', False, 'too long');
  Expect('JBUC4NM9C35SKXVFG3X',    False, 'separators missing');
  Expect('JBUC-4NM9C-35SKX-VFG3O', False, 'letter O is not in the alphabet');
  Expect('JBUC-4NM9C-35SKX-VFG31', False, 'digit 1 is not in the alphabet');
  Expect('JBUC-4NM9C-35SKX-VFG3!', False, 'punctuation');
  Expect('',                       False, 'empty');

  { The check character is weighted by position precisely so that these fail.
    Transposing two characters is the commonest mistake made copying a key,
    and an unweighted sum would accept it. }
  Expect('JBUC-N4M9C-35SKX-VFG3X', False, 'first two characters transposed');
  Expect('JBUC-4NM9C-35SKX-VF3GX', False, 'two characters transposed near the end');

  if Failures = 0 then
    Log := Log + #13#10 + 'ALL PASSED'
  else
    Log := Log + #13#10 + 'FAILURES: ' + IntToStr(Failures);

  SaveStringToFile(ExpandConstant('{param:out|' + ExpandConstant('{tmp}') + '\serial-test.txt}'), Log, False);

  { Abort: this exists to run the checks, not to install anything. }
  Result := False;
end;
