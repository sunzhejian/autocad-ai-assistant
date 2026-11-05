;;; Run APPLOAD, select this file, then enter CADAIUI.
;;; Restores visible tools and file dialogs without resetting a profile or DWG.
(defun c:CADAIUI (/ setting)
  (foreach setting '(("FILEDIA" . 1) ("CMDDIA" . 1) ("MENUBAR" . 1) ("STATUSBAR" . 1))
    (setvar (car setting) (cdr setting)))
  (command "_.CLEANSCREENOFF")
  (command "_.RIBBON")
  (command "_.COMMANDLINE")
  (command "_.FILETAB")
  (princ "\nCAD interface restored. Double-click the active ribbon tab if its panels are collapsed.")
  (princ))
(princ "\nEnter CADAIUI to restore the CAD interface.")
(princ)
