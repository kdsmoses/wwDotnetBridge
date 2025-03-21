SET SAFETY OFF
*!*	 set to your path
SET PATH TO "C:\Users\seank\source\wwDotnetBridge\Distribution"
DO wwDotNetBridge
LOCAL loBridge as wwDotNetBridge
loBridge = CreateObject("wwDotNetBridge","V4")
loBridge.EnableThrowOnError()
loBridge.LoadAssembly("C:\Users\seank\source\wwDotnetBridge\Tests\bin\Debug\wwDotNetBridge.Tests.dll")

? "Loopback test"
loLoopback = loBridge.CreateInstance("Westwind.WebConnection.Tests.Loopback")
loLoopbackHandler = CREATEOBJECT("LoopbackEventHandler")
loLoopbackEventSubscription = loBridge.SubscribeToEvents(loLoopback, loLoopbackHandler)
loLoopback.Raise()
loLoopbackEventSubscription.Unsubscribe()

? "File system watcher test"
lcDir = GETENV("TEMP")
IF NOT Directory(lcDir)
   MD lcDir
ENDIF
loFw = loBridge.CreateInstance("System.IO.FileSystemWatcher", lcDir)
loFwHandler = CREATEOBJECT("FwEventHandler")
loFwSubscription = loBridge.SubscribeToEvents(loFw, loFwHandler)
loFw.EnableRaisingEvents = .T.

lcFile = lcDir + "\wwDotNetBridge.EventTest.txt"
STRTOFILE("DDD",lcFile)
STRTOFILE("FFF",lcFile)
loFwSubscription.Unsubscribe()
RETURN


DEFINE CLASS LoopbackEventHandler as Custom

PROCEDURE OnNoParams()
 ? "Loopback test event raised: NoParams"
ENDPROC

PROCEDURE OnTwoParams(a,b)
 ? "Loopback test event raised: TwoParams: "  + a + STR(b)
ENDPROC

ENDDEFINE


DEFINE CLASS FwEventHandler as Custom

FUNCTION OnCreated(sender,ev)
? "FILE CREATED: "
?  ev.FullPath
ENDFUNC

FUNCTION OnChanged(sender,ev)
? "FILE CHANGE: "
?  ev.FullPath
ENDFUNC

FUNCTION OnDeleted(sender, ev)
? "FILE DELETED: "
?  ev.FullPath
ENDFUNC

FUNCTION OnRenamed(sender, ev)
LOCAL lcOldPath, lcPath

? "FILE RENAMED: " 
loBridge = GetwwDotnetBridge()

*** Not sure why these require indirect referencing but they do
lcOldPath = loBridge.GetProperty(ev,"OldFullPath")
lcPath = loBridge.GetProperty(ev,"FullPath")
? lcOldPath + " -> " + lcPath

ENDFUNC

ENDDEFINE
