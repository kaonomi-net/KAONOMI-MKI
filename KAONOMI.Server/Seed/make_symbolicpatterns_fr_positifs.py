"""
Generates symbolicpatterns-fr-positifs.json: 200 symbolic patterns (PUT /symbolicpattern) for French words of life,
birth and joy. Each entry: the symbol is the first word; the regexes match that word, its inflections and up to five
synonyms (case ignored, whole words, accents optional); the signal !HAPPY, no event; the title, tags and comment
follow the meaning of the words; language FR. The ids are stable (fr-happy-<symbol>).

Usage: python make_symbolicpatterns_fr_positifs.py  (writes the JSON next to this script)
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import seedpatterns  # noqa: E402

# symbol | tags | comment | regex bodies (the head word first, then its synonyms), separated by ';'
# In the bodies, an accented letter also matches its plain form (é -> [ée]).
DATA = """
vie|#VIE|La vie, le fait d'exister.|vie(s)?;existence(s)?;joie de vivre;goût de vivre;pleine vie
vivre|#VIE|Être en vie, profiter de l'existence.|vi(vre|vons|vez|vent|vais|vait|vaient|vra|vrons|vront)|vécu(e|s|es)?;exist(er|e|ent|ait);profit(er|e|ons|ez|ent) de la vie
vivant|#VIE|Ce qui est en vie, plein de vie.|vivant(e|s|es)?;en vie;bien vivant(e|s|es)?;animé(e|s|es)?;plein(e|s|es)? de vie
naissance|#NAISSANCE #VIE|La venue au monde d'un être.|naissance(s)?;venue au monde;mise au monde;faire-part de naissance;natalité(s)?
naître|#NAISSANCE #VIE|Venir au monde.|naître;naît;naiss(ent|ait|ance)|né(e|s|es)? (le|en|à|hier|ce matin|aujourd'hui)|vu le jour;venu(e|s|es)? au monde
bébé|#NAISSANCE #ENFANCE|Un tout petit enfant.|bébé(s)?;poupon(s)?;bambin(e|s|es)?;petit bout;tout-petit(s)?
nouveau-né|#NAISSANCE #ENFANCE|L'enfant qui vient de naître.|nouveau-né(e|s|es)?;nouveaux-nés;nouvelle-née;petit dernier;petite dernière
nourrisson|#NAISSANCE #ENFANCE|L'enfant qui tète encore.|nourrisson(s)?;nouveau venu;nouvelle venue;petit ange
grossesse|#NAISSANCE #FAMILLE|L'état de la femme qui attend un enfant.|grossesse(s)?;attend(re|s|ons|ent) un (enfant|bébé|heureux événement)|heureux événement;future(s)? maman(s)?;futur(s)? papa(s)?
sage-femme|#NAISSANCE|La personne qui aide les femmes à accoucher.|sage(s)?-femme(s)?;maïeuticien(ne|s|nes)?;suivi de grossesse;échographie(s)?
accouchement|#NAISSANCE|La mise au monde d'un enfant.|accouchement(s)?;accouch(er|é|ée|e|ent|ait)|enfantement(s)?;donner la vie;donné la vie
maternité|#NAISSANCE #FAMILLE|Le fait d'être mère.|maternité(s)?;maternel(le|s|les)?;devenir maman;devenue maman;jeune(s)? maman(s)?
paternité|#NAISSANCE #FAMILLE|Le fait d'être père.|paternité(s)?;paternel(le|s|les)?;devenir papa;devenu papa;jeune(s)? papa(s)?
enfant|#ENFANCE #FAMILLE|Un être humain dans ses premières années.|enfant(s)?;gamin(e|s|es)?;marmot(s)?;petit(e|s|es)? (garçon|fille)(s)?;bout(s)? de chou
enfance|#ENFANCE|Les premières années de la vie.|enfance(s)?;enfantin(e|s|es)?;innocence(s)?;âge tendre;cour de récré(ation)?
berceau|#NAISSANCE #ENFANCE|Le petit lit d'un bébé.|berceau(x)?;berc(er|é|ée|ement)|landau(s)?;couffin(s)?;layette(s)?
baptême|#NAISSANCE #FETE|La fête qui accueille un enfant.|baptême(s)?;baptis(er|é|ée)|parrain(s)?;marraine(s)?;filleul(e|s|es)?
allaitement|#NAISSANCE #ENFANCE|Le fait de nourrir un bébé au sein.|allaitement(s)?;allait(er|e|é|ée)|biberon(s)?;tétée(s)?;donner le sein
croissance|#VIE #NATURE|Le fait de grandir, de se développer.|croissance(s)?;développement(s)?;essor(s)?;épanoui(e|s|es)?
grandir|#ENFANCE #VIE|Devenir plus grand, mûrir.|grand(ir|it|is|issent|issait|i|ie)|mûrir;mûri(e|s|es)?;premiers pas;premier(s)? mot(s)?
anniversaire|#FETE #VIE|Le jour où l'on fête sa naissance.|anniversaire(s)?;joyeux anniversaire;bon anniversaire;bougie(s)? d'anniversaire;souffler (les|ses) bougies
jeunesse|#VIE #ENFANCE|Le temps de la vie où l'on est jeune.|jeunesse(s)?;juvénile(s)?;fraîcheur(s)?;printemps de la vie;jeune(s)? et plein(e|s|es)? d'énergie
famille|#FAMILLE|Les parents et leurs enfants.|famille(s)?;familial(e|es|aux)?;foyer(s)?;les miens;réunion(s)? de famille
maman|#FAMILLE|La mère, avec tendresse.|maman(s)?;mère(s)?;mamounette;maternelle(s)?
papa|#FAMILLE|Le père, avec tendresse.|papa(s)?;père(s)?;paternel(s)?;papounet
grands-parents|#FAMILLE|Les parents des parents.|grands?-parents?;grand-mère(s)?;grand-père(s)?;mamie(s)?;papi(s)?
adoption|#FAMILLE #NAISSANCE|Le fait d'accueillir un enfant comme le sien.|adoption(s)?;adopt(er|é|ée|és|ées|if|ive)|famille(s)? d'accueil;accueillir un enfant
renaissance|#VIE #ESPOIR|Le fait de renaître, un nouveau départ.|renaissance(s)?;renaître;renaît;nouveau départ;nouvelle vie;second souffle
fertilité|#NAISSANCE #NATURE|La capacité à donner la vie.|fertilité(s)?;fertile(s)?;fécond(e|s|es|ité)?;fécondation(s)?;prolifique(s)?
vitalité|#VIE #SANTE|L'énergie de la vie.|vitalité(s)?;vital(e|es|aux)?;dynamisme(s)?;pétillant(e|s|es)?;plein(e|s|es)? d'entrain
bonheur|#JOIE|L'état de celui qui est pleinement heureux.|bonheur(s)?;bonne fortune;bénéfique(s)?;comblé(e|s|es)?;aux anges
heureux|#JOIE|Qui ressent du bonheur.|heureu(x|se|ses|sement);happy;épanoui(e|s|es)?;radieu(x|se|ses);rayonnant(e|s|es)?
joie|#JOIE|Un sentiment vif de plaisir.|joie(s)?;réjoui(r|e|s|es|ssance|ssances)?;liesse(s)?;gaîté(s)?;exultation(s)?
joyeux|#JOIE|Qui exprime la joie.|joyeu(x|se|ses|sement);jovial(e|es|aux)?;guilleret(te|s|tes)?;enjoué(e|s|es)?;de bonne humeur
gaieté|#JOIE|La bonne humeur, l'entrain.|gaieté(s)?;gai(e|s|es)?;allègre(s)?;pétillant(e|s|es)?;espiègle(s)?
rire|#JOIE #HUMOUR|Exprimer la gaieté par le rire.|ri(re|t|ent|ait|aient|ons|ez)|rigol(er|e|ent|ade|ades|o|ote)|éclat(s)? de rire;fou rire;hilare(s)?
sourire|#JOIE|L'expression du visage qui montre le plaisir.|sourire(s)?;sour(it|ient|iait|iant|iante|ions)|souriant(e|s|es)?;ravi(e|s|es)? (de|d')
humour|#JOIE #HUMOUR|La capacité à faire rire.|humour(s)?;drôle(s)?;amusant(e|s|es)?;comique(s)?;blague(s)?
plaisir|#JOIE|Ce qui fait du bien, réjouit.|plaisir(s)?;agréable(s)?;délectation(s)?;savour(er|e|é|ée)|régal(e|er|é)?
allégresse|#JOIE|Une joie vive qui se manifeste.|allégresse(s)?;exaltation(s)?;transport(s)? de joie;fou de joie;folle de joie
euphorie|#JOIE|Un sentiment intense de bien-être.|euphorie(s)?;euphorique(s)?;exalté(e|s|es)?;sur un petit nuage;au septième ciel
enthousiasme|#JOIE #MOTIVATION|Une ardeur joyeuse.|enthousiasme(s)?;enthousiaste(s)?;emball(é|ée|és|ées)|passionnant(e|s|es)?;génial(e|es|aux)?
ravi|#JOIE|Très content.|ravi(e|s|es)?;enchanté(e|s|es)?;aux anges;transporté(e|s|es)? de joie;charmé(e|s|es)?
content|#JOIE|Satisfait, heureux de quelque chose.|content(e|s|es)?;satisfait(e|s|es)?;comblé(e|s|es)?;heureux de;heureuse de
satisfaction|#JOIE #REUSSITE|Le plaisir de ce qui est réussi.|satisfaction(s)?;satisfaisant(e|s|es)?;contentement(s)?;plénitude(s)?
sérénité|#BIENETRE #PAIX|Un calme intérieur.|sérénité(s)?;serein(e|s|es)?;apaisé(e|s|es)?;quiétude(s)?;zen
bien-être|#BIENETRE|L'état agréable du corps et de l'esprit.|bien-être;bien dans (ma|sa|ta|leur|notre|votre) peau;confort(s)?;douceur(s)? de vivre;cocooning
épanouissement|#BIENETRE #VIE|Le développement heureux de soi.|épanouissement(s)?;épanou(ir|i|ie|is|ies|issant|issante);accomplissement(s)? personnel(s)?;plénitude(s)?
émerveillement|#JOIE|Le fait d'être émerveillé.|émerveillement(s)?;émerveill(er|é|ée|és|ées|ant|ante)|ébloui(e|s|es|ssant|ssante)?;fasciné(e|s|es)?;wow
enchantement|#JOIE|Un ravissement, une magie.|enchantement(s)?;enchanteu(r|rs|resse)|magique(s)?;féerique(s)?;merveilleu(x|se|ses)
jubilation|#JOIE|Une joie débordante.|jubilation(s)?;jubil(er|e|ent|ait|ant|ante)|exult(er|e|ent)|sauter de joie
félicité|#JOIE|Un bonheur parfait.|félicité(s)?;bonheur parfait;nirvana;paradis sur terre
béatitude|#JOIE #PAIX|Un bonheur calme et parfait.|béatitude(s)?;béat(e|s|es)?;extase(s)?;ravissement(s)?
optimisme|#ESPOIR|La tendance à voir le bon côté.|optimisme(s)?;optimiste(s)?;positi(f|fs|ve|ves|vité);voir le verre à moitié plein;bon côté des choses
entrain|#JOIE #VIE|Une gaieté pleine d'énergie.|entrain;avoir la pêche;avoir la patate;en forme;plein(e|s|es)? d'énergie
fierté|#REUSSITE #JOIE|La satisfaction d'avoir bien fait.|fierté(s)?;fi(er|ère|ers|ères) de;honneur(s)?;digne(s)? d'éloges
soulagement|#BIENETRE #ESPOIR|La fin d'une inquiétude.|soulagement(s)?;soulag(é|ée|és|ées|er|eant)|ouf;délivr(é|ée|és|ance)|rassuré(e|s|es)?
délice|#JOIE #PLAISIR|Un plaisir très vif.|délice(s)?;délicieu(x|se|ses);savoureu(x|se|ses);exquis(e|es)?;succulent(e|s|es)?
merveille|#JOIE|Une chose admirable.|merveille(s)?;splendide(s)?;magnifique(s)?;sublime(s)?;formidable(s)?
chance|#JOIE #REUSSITE|Un sort favorable.|chance(s)?;chanceu(x|se|ses);veinard(e|s|es)?;bonne étoile;coup de bol
amour|#AMOUR|Un sentiment profond d'affection.|amour(s)?;je t'aime;mon cœur;ma chérie;mon chéri
aimer|#AMOUR|Éprouver de l'amour, de l'affection.|aim(er|e|es|ons|ez|ent|ais|ait|é|ée|és|ées)|ador(er|e|ent|é|ée|able|ables)|chér(ir|i|ie|is|ies)|affectionn(er|e|é|ée)
tendresse|#AMOUR|Une affection douce.|tendresse(s)?;tendre(s)?;doux et tendre;douceur(s)?;attendri(e|s|es|ssant|ssante)?
câlin|#AMOUR #FAMILLE|Un geste tendre où l'on serre quelqu'un.|câlin(s)?;câlin(er|é|ée)|étreinte(s)?;enlacé(e|s|es)?;dans (mes|ses|tes) bras
bisou|#AMOUR #FAMILLE|Un petit baiser affectueux.|bisou(s)?;bise(s)?;bécot(s)?;smack;gros bisous
mariage|#AMOUR #FETE|L'union de deux personnes.|mariage(s)?;mari(er|é|ée|és|ées) (avec|hier|demain|aujourd'hui)|épous(er|é|ée)|union(s)? civile(s)?;vivent les mariés
noces|#AMOUR #FETE|La fête du mariage.|noce(s)?;nuptial(e|es|aux)?;lune de miel;voyage de noces;noces d'or
fiançailles|#AMOUR #FETE|La promesse de mariage.|fiançailles;fiancé(e|s|es)?;demande en mariage;bague de fiançailles
couple|#AMOUR|Deux personnes qui s'aiment.|couple(s)?;âme(s)? sœur(s)?;ma moitié;en amoureux;vie à deux
passion|#AMOUR|Un amour intense.|passion(s)?;passionné(e|s|es)?;fou amoureux;folle amoureuse
romantique|#AMOUR|Tendre et sentimental.|romantique(s)?;romantisme(s)?;dîner aux chandelles;sentimental(e|es|aux)?;idylle(s)?
amoureux|#AMOUR|Qui aime d'amour.|amoureu(x|se|ses|sement);épris(e|es)?;coup de foudre;tomb(er|é|ée) amoureu(x|se);béguin(s)?
amitié|#AMITIE|Un lien d'affection entre amis.|amitié(s)?;amical(e|es|aux|ement)?;fraternité(s)?;camaraderie(s)?;sororité(s)?
ami|#AMITIE|Une personne liée par l'amitié.|ami(e|s|es)?;copain(s)?;copine(s)?;pote(s)?;meilleur(e|s|es)? ami(e|s|es)?
complicité|#AMITIE #AMOUR|Une entente profonde.|complicité(s)?;complice(s)?;entente(s)?;connivence(s)?;au diapason
affection|#AMOUR #AMITIE|Un sentiment tendre envers quelqu'un.|affection(s)?;affectueu(x|se|ses|sement);attachement(s)?
bienveillance|#AMITIE #PAIX|La volonté du bien d'autrui.|bienveillance(s)?;bienveillant(e|s|es)?;attentionné(e|s|es)?;prévenant(e|s|es)?;altruisme(s)?
gentillesse|#AMITIE|Une douceur aimable.|gentillesse(s)?;gentil(le|s|les)?;aimable(s)?;adorable(s)?;charmant(e|s|es)?
générosité|#AMITIE|Le fait de donner volontiers.|générosité(s)?;généreu(x|se|ses);don(s)? de soi;cœur sur la main;largesse(s)?
partage|#AMITIE|Le fait de partager avec d'autres.|partage(s)?;partag(er|eons|ent|eait|eaient)|mettre en commun;convivial(e|es|aux|ité)?
solidarité|#AMITIE #PAIX|L'aide mutuelle.|solidarité(s)?;solidaire(s)?;fraternel(le|s|les)?;main tendue;soutien(s)?
entraide|#AMITIE|L'aide que l'on se donne.|entraide(s)?;s'entraider;coup de main;aid(er|é|ée) (un|une) ami(e)?;bénévolat(s)?
réconciliation|#AMITIE #PAIX|Le fait de se réconcilier.|réconciliation(s)?;réconcili(er|é|ée|és|ées)|faire la paix;pardon(s)?;pardonn(er|é|ée)
retrouvailles|#AMITIE #FAMILLE|Le fait de se retrouver après une absence.|retrouvailles;se retrouver;retrouvé(e|s|es)? enfin;réunion(s)? familiale(s)?;réunis
confiance|#AMITIE #ESPOIR|Le fait de se fier à quelqu'un.|confiance(s)?;confiant(e|s|es)?;fiable(s)?;loyauté(s)?;fidélité(s)?
fête|#FETE|Un moment de réjouissance.|fête(s)?;fêt(er|é|ée|ons|ent)|festivité(s)?;soirée(s)? festive(s)?;faire la fête
célébration|#FETE|Le fait de célébrer un événement.|célébration(s)?;célébr(er|é|ée|ons|ent)|commémor(er|ation) heureuse;cérémonie(s)? joyeuse(s)?
festival|#FETE|Une grande fête artistique.|festival(s)?;festivalier(s)?;kermesse(s)?;fête foraine;guinguette(s)?
réveillon|#FETE|Le repas de fête de Noël ou du Nouvel An.|réveillon(s)?;nouvel an;bonne année;meilleurs vœux;saint-sylvestre
noël|#FETE #FAMILLE|La fête de la Nativité.|noël(s)?;joyeux noël;sapin(s)? de noël;père noël;nativité
cadeau|#FETE|Un présent offert.|cadeau(x)?;présent(s)? offert(s)?;offr(ir|e|ent|ait) (un|des) cadeau(x)?;surprise(s)?;étrenne(s)?
surprise|#FETE #JOIE|Un événement inattendu et heureux.|surprise(s)?;surpris(e|es)? et (ravi|ravie|heureux|heureuse);bonne nouvelle;agréable surprise
félicitations|#FETE #REUSSITE|Des compliments pour un succès.|félicitation(s)?;félicit(er|e|é|ée|ons)|compliment(s)?;chapeau bas;hourra
bravo|#FETE #REUSSITE|Une acclamation d'approbation.|bravo(s)?;bien joué;super boulot;génial;youpi
champagne|#FETE|Le vin des célébrations.|champagne(s)?;sabrer le champagne;trinqu(er|ons|é);à la vôtre;santé !
danse|#FETE #JOIE|Le mouvement du corps en musique.|danse(s)?;dans(er|e|ons|ent|ait|eur|euse|eurs|euses)|bal(s)?;valse(s)?;guinche(r)?
chanson|#FETE #CREATION|Un texte mis en musique.|chanson(s)?;chant(er|e|ons|ent|ait|eur|euse)|refrain(s)?;mélodie(s)?;fredonn(er|e|é)
musique|#FETE #CREATION|L'art des sons.|musique(s)?;musical(e|es|aux)?;concert(s)?;symphonie(s)?;harmonieu(x|se|ses)
festin|#FETE #PLAISIR|Un repas de fête.|festin(s)?;banquet(s)?;gueuleton(s)?;bon repas;ripaille(s)?
carnaval|#FETE|Une fête costumée.|carnaval(s)?;mardi gras;défilé(s)? costumé(s)?;déguisement(s)?;masque(s)? de carnaval
vacances|#FETE #BIENETRE|Le temps de repos et de loisirs.|vacance(s)?;vacancier(s)?;congé(s)? payé(s)?;farniente;évasion(s)?
pique-nique|#FETE #NATURE|Un repas en plein air.|pique-nique(s)?;piqueniqu(e|er)|déjeuner sur l'herbe;barbecue(s)?;apéro(s)?
applaudissement|#FETE #REUSSITE|Des battements de mains d'approbation.|applaudissement(s)?;applaud(ir|it|issent|i|ie)|ovation(s)?;standing ovation;acclam(er|é|ée|ation|ations)
cotillon|#FETE|Les petits objets de fête.|cotillon(s)?;confetti(s)?;serpentin(s)?;ballon(s)? de fête;feu(x)? d'artifice
guirlande|#FETE|La décoration de fête.|guirlande(s)?;lampion(s)?;décoration(s)? de fête;illumination(s)?;paillette(s)?
santé|#SANTE|Le bon état du corps et de l'esprit.|santé(s)?;bonne santé;en pleine forme;bien portant(e|s|es)?;pétant(e|s|es)? de santé
guérison|#SANTE #ESPOIR|Le retour à la santé.|guérison(s)?;guér(ir|i|ie|is|ies|it|issent)|tiré(e|s|es)? d'affaire;hors de danger;sauvé(e|s|es)?
rétablissement|#SANTE|Le retour à un bon état.|rétablissement(s)?;rétabli(e|s|es)?;remis(e|es)? sur pied;de nouveau sur pied;prompt rétablissement
convalescence|#SANTE|La période où l'on se remet.|convalescence(s)?;convalescent(e|s|es)?;sur la voie de la guérison;récupération(s)?;rémission(s)?
énergie|#SANTE #VIE|La force qui permet d'agir.|énergie(s)?;énergique(s)?;tonus;dynamique(s)?;revigoré(e|s|es)?
vigueur|#SANTE|La force physique, la robustesse.|vigueur(s)?;vigoureu(x|se|ses);costaud(e|s|es)?;fringant(e|s|es)?;en forme olympique
remède|#SANTE|Ce qui guérit.|remède(s)?;traitement(s)? efficace(s)?;antidote(s)?;baume(s)?;panacée(s)?
soin|#SANTE #BIENETRE|L'attention portée au bien-être.|soin(s)?;prendre soin;choy(er|é|ée)|dorlot(er|é|ée)|bichonn(er|é|ée)
miracle|#ESPOIR #JOIE|Un fait extraordinaire et heureux.|miracle(s)?;miraculeu(x|se|ses|sement);prodige(s)?;providentiel(le|s|les)?;inespéré(e|s|es)?
longévité|#SANTE #VIE|Une longue vie.|longévité(s)?;centenaire(s)?;longue vie;bel âge;vieillir en beauté
robuste|#SANTE|Fort et résistant.|robuste(s)?;solide(s)? comme un roc;increvable(s)?;résistant(e|s|es)?;endurant(e|s|es)?
sain|#SANTE|En bonne santé, bon pour la santé.|sain(e|s|es)?;sainement;salubre(s)?;équilibré(e|s|es)?;bio
détente|#BIENETRE|Le relâchement agréable.|détente(s)?;détendu(e|s|es)?;décontracté(e|s|es)?;relax(e|é|ée)?;cool
repos|#BIENETRE #SANTE|Le temps où l'on se repose.|repos;repos(er|é|ée|ant|ante)|sieste(s)?;grasse matinée;ressourc(er|é|ée|ement)
relaxation|#BIENETRE|Une méthode pour se détendre.|relaxation(s)?;méditation(s)?;yoga(s)?;massage(s)?;spa(s)?
printemps|#NATURE #VIE|La saison où la nature renaît.|printemps;printanier(s)?;printanière(s)?;beaux jours;retour des hirondelles
soleil|#NATURE #JOIE|L'astre qui donne la lumière et la chaleur.|soleil(s)?;ensoleillé(e|s|es)?;rayon(s)? de soleil;radieux soleil;soleil levant
lumière|#NATURE #ESPOIR|Ce qui éclaire.|lumière(s)?;lumineu(x|se|ses);éclat(s)?;rayonnement(s)?;clarté(s)?
fleur|#NATURE|L'organe coloré des plantes.|fleur(s)?;fleuri(r|e|s|es)?;bouquet(s)?;rose(s)? rouge(s)?;pétale(s)?
floraison|#NATURE #VIE|Le moment où les plantes fleurissent.|floraison(s)?;éclosion des fleurs;en fleurs;fleurissement(s)?;cerisier(s)? en fleurs
bourgeon|#NATURE #VIE|La pousse qui va devenir feuille ou fleur.|bourgeon(s)?;bourgeonn(er|e|ent|ant|ement)|jeune(s)? pousse(s)?;rameau(x)?
éclosion|#NATURE #NAISSANCE|Le fait d'éclore, de sortir de l'œuf.|éclosion(s)?;éclo(re|t|sent|s|se)|sorti(e|s|es)? de l'œuf;poussin(s)?;oisillon(s)?
germination|#NATURE #VIE|Le développement d'une graine.|germination(s)?;germ(er|e|ent|é|ée)|semis;semer;sem(é|ée|és|ées)
graine|#NATURE #VIE|La semence qui donnera une plante.|graine(s)?;semence(s)?;pépin(s)?;planter (une|des) graine(s)?
arbre|#NATURE #VIE|Une grande plante à tronc.|arbre(s)?;forêt(s)?;feuillage(s)?;chêne(s)?;verger(s)?
jardin|#NATURE #BIENETRE|Un terrain cultivé de fleurs et légumes.|jardin(s)?;jardin(er|age|ier|ière)|potager(s)?;parterre(s)? de fleurs;serre(s)?
nature|#NATURE|Le monde vivant.|nature(s)?;naturel(le|s|les)?;biodiversité(s)?;écosystème(s)?;grand air
aube|#NATURE #ESPOIR|Le début du jour.|aube(s)?;point du jour;petit matin;lever du jour;premières lueurs
aurore|#NATURE #ESPOIR|La lumière qui précède le lever du soleil.|aurore(s)?;aurore(s)? boréale(s)?;lever de soleil;aube nouvelle
arc-en-ciel|#NATURE #JOIE|Un arc coloré dans le ciel.|arc(s)?-en-ciel;arcs-en-ciel;mille couleurs;multicolore(s)?;chatoyant(e|s|es)?
plage|#NATURE #BIENETRE|Le bord de mer où l'on se baigne.|plage(s)?;bord de mer;baignade(s)?;sable chaud;estival(e|es|aux)?
renouveau|#NATURE #ESPOIR|Un retour à la vie, un renouvellement.|renouveau(x)?;renouvellement(s)?;regain(s)?;nouveau souffle;revivre
verdure|#NATURE|L'ensemble des plantes vertes.|verdure(s)?;verdoyant(e|s|es)?;prairie(s)?;pâturage(s)?;herbe(s)? fraîche(s)?
oiseau|#NATURE|Un animal à plumes qui chante.|oiseau(x)?;chant des oiseaux;gazouill(is|er|ement)|hirondelle(s)?;rossignol(s)?
papillon|#NATURE|Un insecte aux ailes colorées.|papillon(s)?;chenille(s)?;chrysalide(s)?;libellule(s)?;coccinelle(s)?
nid|#NATURE #NAISSANCE|L'abri que construisent les oiseaux.|nid(s)?;nichée(s)?;couvée(s)?;nidification(s)?;œuf(s)? de pâques
récolte|#NATURE #REUSSITE|Le fait de cueillir les fruits du travail.|récolte(s)?;récolt(er|é|ée)|cueillette(s)?;cueill(ir|i|ie)|fruits du travail
moisson|#NATURE #REUSSITE|La récolte des céréales.|moisson(s)?;moissonn(er|é|ée)|blé(s)? mûr(s)?;vendange(s)?;épis
étoile|#NATURE #ESPOIR|Un astre qui brille la nuit.|étoile(s)?;étoilé(e|s|es)?;ciel étoilé;voie lactée;étoile(s)? filante(s)?
paysage|#NATURE #BIENETRE|Une étendue de pays que l'on contemple.|paysage(s)?;panorama(s)?;vue imprenable;coucher de soleil;horizon(s)?
réussite|#REUSSITE|Le fait de réussir.|réussite(s)?;réuss(ir|i|ie|is|ies|it|issent)|aboutissement(s)?;mission accomplie;objectif(s)? atteint(s)?
succès|#REUSSITE|Un résultat heureux.|succès;fructueu(x|se|ses);couronné(e|s|es)? de succès;carton plein;percée(s)?
victoire|#REUSSITE|Le fait de gagner.|victoire(s)?;victorieu(x|se|ses);vainqueur(s)?;remport(er|é|ée)|battu le record
triomphe|#REUSSITE|Un succès éclatant.|triomphe(s)?;triomph(er|é|ant|ante|al|ale)|consécration(s)?;apothéose(s)?;gloire(s)?
exploit|#REUSSITE|Une action remarquable.|exploit(s)?;prouesse(s)?;performance(s)? remarquable(s)?;haut fait;tour de force
médaille|#REUSSITE|Une récompense pour un mérite.|médaille(s)?;médaillé(e|s|es)?;médaille d'or;podium(s)?;décoré(e|s|es)?
champion|#REUSSITE|Le vainqueur d'une compétition.|champion(ne|s|nes)?;championnat(s)? gagné(s)?;premi(er|ère) du classement;numéro un
diplôme|#REUSSITE #APPRENTISSAGE|Un titre obtenu par un examen.|diplôme(s)?;diplômé(e|s|es)?;reçu(e|s|es)? (à|au) (l'examen|bac|concours)|bac en poche;mention très bien
promotion|#REUSSITE|Une montée en grade.|promotion(s)?;promu(e|s|es)?;avancement(s)?;augmentation(s)? de salaire;nouveau poste
accomplissement|#REUSSITE|Le fait d'accomplir pleinement.|accomplissement(s)?;accompl(ir|i|ie|is|ies)|réalisation(s)?;concrétis(er|é|ée|ation)
progrès|#REUSSITE #APPRENTISSAGE|Le fait d'aller vers le mieux.|progrès;progress(er|e|ent|é|ion|ions)|amélioration(s)?;avancée(s)?;s'améliorer
performance|#REUSSITE|Un résultat remarquable.|performance(s)?;performant(e|s|es)?;brillant(e|s|es)?;excellent(e|s|es)? résultat(s)?
record|#REUSSITE|Une performance jamais atteinte.|record(s)?;battre (le|son|un) record;record du monde;inédit(e|s|es)?;historique(s)?
trophée|#REUSSITE|Un objet qui récompense une victoire.|trophée(s)?;coupe(s)? (gagnée|remportée)(s)?;palme(s)?;couronne(s)? de lauriers;distinction(s)?
talent|#REUSSITE #CREATION|Une aptitude remarquable.|talent(s)?;talentueu(x|se|ses);doué(e|s|es)?;virtuose(s)?;prodige(s)?
mérite|#REUSSITE|Ce qui rend digne d'estime.|mérite(s)?;mérit(er|é|ée|ant|ante|oire)|bien mérité(e|s|es)?;digne(s)? d'éloges
gagner|#REUSSITE|Être vainqueur, obtenir.|gagn(er|e|es|ons|ez|ent|é|ée|és|ait|ant|ante|ants)|vainc(re|u|ue)|l'emport(er|é)
excellence|#REUSSITE|Le plus haut degré de qualité.|excellence(s)?;excellent(e|s|es)?;remarquable(s)?;exceptionnel(le|s|les)?;parfait(e|s|es)?
récompense|#REUSSITE|Ce qu'on reçoit pour un mérite.|récompense(s)?;récompens(er|é|ée|és);gratification(s)?;lauréat(e|s|es)?;bonus
prospérité|#REUSSITE #ESPOIR|L'état de ce qui est florissant.|prospérité(s)?;prospère(s)?;florissant(e|s|es)?;abondance(s)?;richesse(s)? partagée(s)?
espoir|#ESPOIR|Le fait d'attendre un bien avec confiance.|espoir(s)?;plein(e|s|es)? d'espoir;lueur(s)? d'espoir;encourag(eant|eante|ement|é|ée)|perspective(s)? heureuse(s)?
espérance|#ESPOIR|La confiance en l'avenir.|espérance(s)?;espér(er|ons|ez|ait)|j'espère;souhait(s)?;souhait(er|e|é|ons)
rêve|#ESPOIR #CREATION|Un désir, une aspiration.|rêve(s)?;rêv(er|e|ons|ent|é|ait|eur|euse)|songe(s)?;aspiration(s)?;rêve devenu réalité
avenir|#ESPOIR|Le temps à venir, plein de promesses.|avenir(s)?;futur radieux;lendemain(s)? qui chante(nt)?;demain sera meilleur;promesse(s)?
paix|#PAIX|L'absence de conflit, la tranquillité.|paix;paisible(s)?;pacifique(s)?;tranquillité(s)?;calme retrouvé
harmonie|#PAIX #BIENETRE|Un accord heureux.|harmonie(s)?;harmonieu(x|se|ses);équilibre(s)?;concorde(s)?;accord parfait
liberté|#PAIX #ESPOIR|Le fait d'être libre.|liberté(s)?;libre(s)?;libér(é|ée|és|ées|ation)|émancipation(s)?;indépendance(s)?
gratitude|#GRATITUDE|La reconnaissance d'un bienfait.|gratitude(s)?;reconnaissant(e|s|es)?;grâce(s)?(?! à);rendre grâce;remerciement(s)?
merci|#GRATITUDE|Le mot qui remercie.|merci(s)?;mille mercis;remerci(er|e|é|ée|ons)|thank you;je vous remercie
reconnaissance|#GRATITUDE #REUSSITE|Le fait d'être reconnu, la gratitude.|reconnaissance(s)?;reconnu(e|s|es)? (pour|à sa juste valeur);hommage(s)?
bénédiction|#GRATITUDE #ESPOIR|Une grâce, un bienfait.|bénédiction(s)?;béni(e|s|es|r)?;bienfait(s)?;don du ciel;cadeau du ciel
grâce|#GRATITUDE #BEAUTE|Le charme, la faveur.|grâce(s)?(?! à);gracieu(x|se|ses|sement);élégance(s)?;élégant(e|s|es)?
sagesse|#ESPOIR #PAIX|La connaissance et la mesure.|sagesse(s)?;sage(s)?;clairvoyance(s)?;discernement(s)?;lucidité(s)?
courage|#ESPOIR|La force face aux difficultés.|courage(s)?;courageu(x|se|ses|sement);brave(s)?;bravoure(s)?;vaillant(e|s|es)?
persévérance|#ESPOIR #REUSSITE|Le fait de continuer malgré les obstacles.|persévérance(s)?;persévér(er|é|ant|ante)|ténacité(s)?;tenace(s)?;détermination(s)?
résilience|#ESPOIR|La capacité à se relever.|résilience(s)?;résilient(e|s|es)?;se relever;relevé(e|s|es)? la tête;rebondir
foi|#ESPOIR|Une confiance profonde.|foi;croire en (soi|toi|moi|nous|l'avenir);conviction(s)?;ferveur(s)?;fervent(e|s|es)?
inspiration|#CREATION #ESPOIR|Un élan créateur.|inspiration(s)?;inspir(é|ée|és|ant|ante|er)|muse(s)?;élan(s)? créateur(s)?;déclic(s)?
motivation|#ESPOIR #REUSSITE|Ce qui pousse à agir.|motivation(s)?;motiv(é|ée|és|ées|ant|ante)|stimul(é|ée|ant|ante)|boost(é|ée)?
ambition|#REUSSITE #ESPOIR|Le désir d'accomplir de grandes choses.|ambition(s)?;ambitieu(x|se|ses);grand(s)? dessein(s)?;projet(s)? d'avenir;viser haut
création|#CREATION|Le fait de créer.|création(s)?;cré(er|é|ée|és|ées|ateur|atrice|ateurs)|œuvre(s)?;chef-d'œuvre;chefs-d'œuvre
créativité|#CREATION|La capacité à inventer.|créativité(s)?;créati(f|fs|ve|ves);ingénieu(x|se|ses);inventi(f|ve|vité);original(e|es|aux)?
invention|#CREATION #REUSSITE|Une chose nouvelle imaginée.|invention(s)?;invent(er|é|ée|eur|eurs|rice)|innovation(s)?;innov(er|ant|ante)|trouvaille(s)?
découverte|#CREATION #APPRENTISSAGE|Le fait de découvrir.|découverte(s)?;découvr(ir|e|ent|ait)|découvert(e|s|es)?;révélation(s)?;eurêka
apprendre|#APPRENTISSAGE|Acquérir des connaissances.|appren(dre|ds|d|ons|ez|nent|ait|tissage)|apprentissage(s)?;étudi(er|e|é);connaissance(s)?
curiosité|#APPRENTISSAGE|L'envie de connaître.|curiosité(s)?;curieu(x|se|ses);soif de savoir;intéress(é|ée|ant|ante)|captivant(e|s|es)?
imagination|#CREATION|La faculté d'imaginer.|imagination(s)?;imagin(er|é|ée|atif|ative)|fantaisie(s)?;rêverie(s)?;conte(s)? de fées
art|#CREATION #BEAUTE|L'expression de la beauté.|art(s)?;artiste(s)?;artistique(s)?;peinture(s)?;poésie(s)?
beauté|#BEAUTE|Ce qui est beau.|beauté(s)?;belle(s)?;joli(e|s|es)?;ravissant(e|s|es)?
jeu|#JOIE #ENFANCE|Une activité pour s'amuser.|jeu(x)?;ludique(s)?;amus(er|e|ement|ant|ante)|divertissement(s)?;récréation(s)?
jouer|#JOIE #ENFANCE|S'amuser à un jeu.|jou(er|e|es|ons|ez|ent|ait|aient)|s'amuser;batifol(er|e)|gambad(er|e|ent)|joueu(r|se|rs|ses)
aventure|#VOYAGE #JOIE|Une expérience nouvelle et excitante.|aventure(s)?;aventureu(x|se|ses|rier|rière)|périple(s)?;épopée(s)?;excitant(e|s|es)?
voyage|#VOYAGE #JOIE|Le fait d'aller découvrir d'autres lieux.|voyage(s)?;voyag(er|eur|euse|eurs|e|eons)|escapade(s)?;road trip;tour du monde
exploration|#VOYAGE #APPRENTISSAGE|Le fait d'explorer.|exploration(s)?;explor(er|ateur|atrice|é|ée)|expédition(s)?;contrée(s)? lointaine(s)?;terre(s)? inconnue(s)?
nouveauté|#CREATION #ESPOIR|Ce qui est nouveau.|nouveauté(s)?;nouveau départ;tout neuf;toute neuve;première fois
"""

if __name__ == "__main__":
    seedpatterns.write(DATA, "fr-happy", "!HAPPY", 200, pathlib.Path(__file__).with_name("symbolicpatterns-fr-positifs.json"))
