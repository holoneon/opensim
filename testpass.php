<?php

$pass = '5cad1ae4df42de378aab6cb532198b7e';
$salt = 'b699d57f458601ce519f3636974d3bb0';

$hp = hash('md5',$pass.':'.$salt);
echo hash('md5', $hp.':'.$salt)."\n"; //not it
echo hash('md5', (hash('md5',$hp.':'.$salt)).':'.$salt)."\n";


//366116f834670675a1d2140cc0b20e3f
