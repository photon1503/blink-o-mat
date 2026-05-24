ok * get SQM from filename i.e. 2026-05-17_21-52-37_L_Temp-15.10_Rot292.32_Ecc0.75_3.97_Exp300.00s__SQM_21.29_0000
ok * add filter awareness if multiple filters are used in the same session
ok * add Sky brightness
ok * fix XISF metadata 
ok * add filters (show all, show accepted, show rejected)
ok * add sorting
ok * add option to read subfolders recursively
ok * fix aspect ration on ROI on startup.
ok * OSC de-bayering, STF stretching for OSC images
ok * open in File Explorer
ok * Fix µm label
ok * make filter values editable
ok * add save/load session. save all current settings, including filter values, sorting, etc.  Load session should restore all settings and filters. also save the list of accepted/rejected subs, so that when you load a session, you see the same accepted/rejected subs as when you saved it. also save all metadata incl. thumbnails, roi preview and position. skip scanning the saved images when rescanning the folder, just add the new files.
ok * add reason for rejection

* add play button to preview window to automatically step through frames at a configurable interval (e.g. 1 frame per second)

* expressions like in sfs?  For example exclude stars where star count / sqm < 2@std dev (star count/sqm) and skytemp > -18?
ok * On the scroll bar to the right of the image, with the little squares, worth having rejected subs squares turn red?
ok * And are the squares selectable?  Can you click on one and jump to that image?

* dass man die selektionparameter an/abhaken kann (ich brauche zb fast ausschließlich FWHM arcsec (der wäre super) und die Eccentricity) und diese als Profil speichern kann
