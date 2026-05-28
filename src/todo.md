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

ok * add play button to preview window to automatically step through frames at a configurable interval (e.g. 1 frame per second)
ok * On the scroll bar to the right of the image, with the little squares, worth having rejected subs squares turn red?
ok * And are the squares selectable?  Can you click on one and jump to that image?

Mark:

* expressions like in sfs?  For example exclude stars where star count / sqm < 2@std dev (star count/sqm) and skytemp > -18?

Patrick:

ok •	FWHM arcsec
ok •	Eccentricity Werte - Testergebnisse
ok •	FWHM Werte Berechnung? Testergebnisse
ok •	Systemvergleich CCD Inspector vs. Rejector (Werteberechnungen)

* dass man die selektionparameter an/abhaken kann (ich brauche zb fast ausschließlich FWHM arcsec (der wäre super) und die Eccentricity) und diese als Profil speichern kann
* Einstellungszeile – BW und Pix size einstellbar (falls auch keine fits header)
* Monitor folder for new files and automatically load them into the software. This would be especially useful for users who are capturing images in real-time and want to see the results immediately without having to manually refresh or reload the folder.

•	Optische Elemente – Lesbarkeit/Größe der Bewertungsparameter
•	Ampelsystem: Gewichtung einstellbar?
•	Optional – bereich für werteberechnung definierbar (crop) – z.b. für FWHM, Eccentricity, etc

.) BewertungsProfil anlegen/speichern mit:
.) Bewertungsfilter an/abhaken (alle sollten frei wählbar sein)
.) automatisch dieses laden lassen können (autocalculation oder profil sollte wählbar sein)
.) wenn gewisse Parameter abgewählt sind diese gar nicht anzeigen lassen
.) wenn man in der Liste auf einen Frame klickt sollte das besser ersichtlich sein welcher gewählt ist (beim dicken unterstrich könnte der frame überm/unterm Strich gemeint sein) - vielleicht die Hintergrundfarbe ändern wenn angewählt? und was kann ich dann mehr machen? 
.) der button „Keep“ ist eigentlich verkehrt - bin ich bei den accepted und klick ich da drauf wirft er ihn weg . Sogesehen müsste der Button „Reject frame“ heißen 

Michael:

ok * square icons on vertical sliders turn yellow when selected (active)
v22 * only import LIGHT images (not BIAS, DARK, FLAT)
v22 * add option to import multiple (sub) folders at once
* filter on rejected per slider

Andreas:

* add option to recognice same files 
* add option to loop (start from beginning after reaching the end) when previewing images